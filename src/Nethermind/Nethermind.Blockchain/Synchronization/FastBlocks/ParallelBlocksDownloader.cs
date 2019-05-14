/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Logging;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class ParallelBlocksDownloader
    {
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly IBlockRequestFeed _blockRequestFeed;
        private int _pendingRequests;
        private int _downloadedHeaders;
        private ILogger _logger;

        public ParallelBlocksDownloader(IEthSyncPeerPool syncPeerPool, IBlockRequestFeed nodeDataFeed, ILogManager logManager)
        {
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _blockRequestFeed = nodeDataFeed ?? throw new ArgumentNullException(nameof(nodeDataFeed));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private SemaphoreSlim _semaphore = new SemaphoreSlim(0);

        private int _lastUsefulPeerCount;

        private async Task ExecuteRequest(CancellationToken token, BlockSyncBatch batch)
        {
            SyncPeerAllocation nodeSyncAllocation = _syncPeerPool.Borrow(BorrowOptions.DoNotReplace, "fast blocks");
            try
            {
                ISyncPeer peer = nodeSyncAllocation?.Current?.SyncPeer;
                batch.AssignedPeer = nodeSyncAllocation;
                if (peer != null)
                {
                    Task<BlockHeader[]> getHeadersTask = peer.GetBlockHeaders(batch.HeadersSyncBatch.StartNumber.Value, batch.HeadersSyncBatch.RequestSize, 0, token);
                    await getHeadersTask.ContinueWith(
                        t =>
                        {
                            if (t.IsCompletedSuccessfully)
                            {
                                batch.HeadersSyncBatch.Response = getHeadersTask.Result;
                            }
                        }
                    );
                }

                (BlocksDataHandlerResult Result, int NodesConsumed) result = (BlocksDataHandlerResult.InvalidFormat, 0);
                try
                {
                    result = _blockRequestFeed.HandleResponse(batch);
                    if (result.Result == BlocksDataHandlerResult.BadQuality)
                    {
                        _syncPeerPool.ReportBadPeer(batch.AssignedPeer);
                    }
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Error when handling response", e);
                }

                Interlocked.Add(ref _downloadedHeaders, result.NodesConsumed);
                if (result.NodesConsumed == 0 && peer != null)
                {
                    _syncPeerPool.ReportNoSyncProgress(nodeSyncAllocation);
                }
            }
            finally
            {
                if (nodeSyncAllocation != null)
                {
//                    _logger.Warn($"Free {nodeSyncAllocation?.Current}");
                    _syncPeerPool.Free(nodeSyncAllocation);
                }
            }
        }

        private async Task UpdateParallelism()
        {
            int newUsefulPeerCount = _syncPeerPool.UsefulPeerCount;
            int difference = newUsefulPeerCount - _lastUsefulPeerCount;
            if (difference == 0)
            {
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Node sync parallelism - {_syncPeerPool.UsefulPeerCount} useful peers out of {_syncPeerPool.PeerCount} in total (pending requests: {_pendingRequests} | remaining: {_semaphore.CurrentCount}).");

            if (difference > 0)
            {
                _semaphore.Release(difference);
            }
            else
            {
                for (int i = 0; i < -difference; i++)
                {
                    if (!await _semaphore.WaitAsync(5000))
                    {
                        newUsefulPeerCount++;
                    }
                }
            }

            _lastUsefulPeerCount = newUsefulPeerCount;
        }

        private async Task KeepSyncing(CancellationToken token)
        {
            do
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await UpdateParallelism();
                if (!await _semaphore.WaitAsync(1000, token))
                {
                    continue;
                }

                BlockSyncBatch request = PrepareRequest();
                if (request.HeadersSyncBatch != null)
                {
                    Interlocked.Increment(ref _pendingRequests);
                    if (_logger.IsTrace) _logger.Trace($"Creating new headers request [{request.HeadersSyncBatch.StartNumber}, {request.HeadersSyncBatch.StartNumber + request.HeadersSyncBatch.RequestSize - 1}]");
                    Task task = ExecuteRequest(token, request);
#pragma warning disable 4014
                    task.ContinueWith(t =>
#pragma warning restore 4014
                    {
                        Interlocked.Decrement(ref _pendingRequests);
                        _semaphore.Release();
                    });
                }
                else
                {
                    await Task.Delay(50);
                    _semaphore.Release();
                    if (_logger.IsDebug) _logger.Debug($"DIAG: 0 batches created with {_pendingRequests} pending requests, {_blockRequestFeed.TotalBlocksPending} pending blocks");
                }
            } while (_pendingRequests != 0);

            if (_logger.IsInfo) _logger.Info($"Finished with {_pendingRequests} pending requests and {_lastUsefulPeerCount} useful peers.");
        }

        private BlockSyncBatch PrepareRequest()
        {
            BlockSyncBatch request = _blockRequestFeed.PrepareRequest();
            if (_logger.IsTrace) _logger.Trace($"Pending requests {_pendingRequests}");
            return request;
        }

        public async Task<long> SyncHeaders(CancellationToken token)
        {
            _downloadedHeaders = 0;
            await KeepSyncing(token);
            return _downloadedHeaders;
        }
    }
}
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test.FastBlocks
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class FastHeadersSyncTests
    {
        [Test]
        public async Task Will_fail_if_launched_without_fast_blocks_enabled()
        {
            IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
            BlockTree blockTree = new(memDbProvider.BlocksDb, memDbProvider.HeadersDb, memDbProvider.BlockInfosDb, new ChainLevelInfoRepository(memDbProvider.BlockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
            Assert.Throws<InvalidOperationException>(() => new HeadersSyncFeed(Substitute.For<ISyncModeSelector>(), blockTree, Substitute.For<ISyncPeerPool>(), new SyncConfig(), Substitute.For<ISyncReport>(), LimboLogs.Instance));
        }

        [Test]
        public async Task Can_prepare_3_requests_in_a_row()
        {
            IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
            BlockTree blockTree = new(memDbProvider.BlocksDb, memDbProvider.HeadersDb, memDbProvider.BlockInfosDb, new ChainLevelInfoRepository(memDbProvider.BlockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
            HeadersSyncFeed feed = new(Substitute.For<ISyncModeSelector>(), blockTree, Substitute.For<ISyncPeerPool>(), new SyncConfig { FastSync = true, FastBlocks = true, PivotNumber = "1000", PivotHash = Keccak.Zero.ToString(), PivotTotalDifficulty = "1000" }, Substitute.For<ISyncReport>(), LimboLogs.Instance);
            HeadersSyncBatch? batch1 = await feed.PrepareRequest();
            HeadersSyncBatch? batch2 = await feed.PrepareRequest();
            HeadersSyncBatch? batch3 = await feed.PrepareRequest();
        }

        [Test]
        public async Task Can_prepare_several_request_and_ignore_request_from_previous_sequence()
        {
            IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfHeadersOnly.OfChainLength(501).TestObject;

            BlockTree blockTree = new(memDbProvider.BlocksDb, memDbProvider.HeadersDb, memDbProvider.BlockInfosDb, new ChainLevelInfoRepository(memDbProvider.BlockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);

            ISyncReport syncReport = Substitute.For<ISyncReport>();
            syncReport.FastBlocksHeaders.Returns(new MeasuredProgress());
            syncReport.HeadersInQueue.Returns(new MeasuredProgress());

            BlockHeader pivot = remoteBlockTree.FindHeader(500, BlockTreeLookupOptions.None)!;
            ResettableHeaderSyncFeed feed = new(Substitute.For<ISyncModeSelector>(), blockTree, Substitute.For<ISyncPeerPool>(), new SyncConfig { FastSync = true, FastBlocks = true, PivotNumber = "500", PivotHash = pivot.Hash.Bytes.ToHexString(), PivotTotalDifficulty = pivot.TotalDifficulty!.ToString() }, syncReport, LimboLogs.Instance);
            feed.InitializeFeed();

            void FulfillBatch(HeadersSyncBatch batch)
            {
                batch.Response = remoteBlockTree.FindHeaders(
                    remoteBlockTree.FindHeader(batch.StartNumber, BlockTreeLookupOptions.None)!.Hash, batch.RequestSize, 0,
                    false);
            }

            await feed.PrepareRequest();
            HeadersSyncBatch? batch1 = await feed.PrepareRequest();
            FulfillBatch(batch1);

            feed.Reset();

            await feed.PrepareRequest();
            HeadersSyncBatch? batch2 = await feed.PrepareRequest();
            FulfillBatch(batch2);

            feed.HandleResponse(batch2);
            feed.HandleResponse(batch1);
        }

        [Test]
        public async Task Can_keep_returning_nulls_after_all_batches_were_prepared()
        {
            IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
            BlockTree blockTree = new(memDbProvider.BlocksDb, memDbProvider.HeadersDb, memDbProvider.BlockInfosDb, new ChainLevelInfoRepository(memDbProvider.BlockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
            HeadersSyncFeed feed = new(Substitute.For<ISyncModeSelector>(), blockTree, Substitute.For<ISyncPeerPool>(), new SyncConfig { FastSync = true, FastBlocks = true, PivotNumber = "1000", PivotHash = Keccak.Zero.ToString(), PivotTotalDifficulty = "1000" }, Substitute.For<ISyncReport>(), LimboLogs.Instance);
            for (int i = 0; i < 10; i++)
            {
                await feed.PrepareRequest();
            }

            var result = await feed.PrepareRequest();
            result.Should().BeNull();
        }

        [Test]
        public async Task Finishes_when_all_downloaded()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1000).TestObject);
            ISyncReport report = Substitute.For<ISyncReport>();
            report.HeadersInQueue.Returns(new MeasuredProgress());
            MeasuredProgress measuredProgress = new();
            report.FastBlocksHeaders.Returns(measuredProgress);
            HeadersSyncFeed feed = new(Substitute.For<ISyncModeSelector>(), blockTree, Substitute.For<ISyncPeerPool>(), new SyncConfig { FastSync = true, FastBlocks = true, PivotNumber = "1000", PivotHash = Keccak.Zero.ToString(), PivotTotalDifficulty = "1000" }, report, LimboLogs.Instance);
            await feed.PrepareRequest();
            blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).TestObject);
            var result = await feed.PrepareRequest();
            result.Should().BeNull();
            feed.CurrentState.Should().Be(SyncFeedState.Finished);
            measuredProgress.HasEnded.Should().BeTrue();
        }

        private class ResettableHeaderSyncFeed : HeadersSyncFeed
        {
            public ResettableHeaderSyncFeed(ISyncModeSelector syncModeSelector, IBlockTree? blockTree, ISyncPeerPool? syncPeerPool, ISyncConfig? syncConfig, ISyncReport? syncReport, ILogManager? logManager, bool alwaysStartHeaderSync = false) : base(syncModeSelector, blockTree, syncPeerPool, syncConfig, syncReport, logManager, alwaysStartHeaderSync)
            {
            }

            public void Reset()
            {
                base.PostFinishCleanUp();
                InitializeFeed();
            }
        }

    }
}

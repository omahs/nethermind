// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Db.Rpc
{
    public class RpcDb : IDb
    {
        private readonly string _dbName;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;
        private readonly IJsonRpcClient _rpcClient;
        private readonly IDb _recordDb;

        public RpcDb(string dbName, IJsonSerializer jsonSerializer, IJsonRpcClient rpcClient, ILogManager logManager, IDb recordDb)
        {
            _dbName = dbName;
            _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _recordDb = recordDb;
        }

        public void Dispose()
        {
            _logger.Info($"Disposing RPC DB {Name}");
            _recordDb.Dispose();
        }

        public string Name { get; } = "RpcDb";

        public byte[] this[byte[] key]
        {
            get => GetThroughRpc(key);
            set => throw new InvalidOperationException("RPC DB does not support writes");
        }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys] => keys.Select(k => new KeyValuePair<byte[], byte[]>(k, GetThroughRpc(k))).ToArray();

        public void Remove(byte[] key)
        {
            throw new InvalidOperationException("RPC DB does not support writes");
        }

        public bool KeyExists(byte[] key)
        {
            return GetThroughRpc(key) is not null;
        }

        public IDb Innermost => this; // record db is just a helper DB here
        public void Flush() { }
        public void Clear() { }

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => _recordDb.GetAll();

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => _recordDb.GetAllValues();

        public IBatch StartBatch()
        {
            throw new InvalidOperationException("RPC DB does not support writes");
        }

        private byte[] GetThroughRpc(byte[] key)
        {
            string responseJson = _rpcClient.Post("debug_getFromDb", _dbName, key.ToHexString()).Result;
            JsonRpcSuccessResponse response = _jsonSerializer.Deserialize<JsonRpcSuccessResponse>(responseJson);

            byte[] value = null;
            if (response.Result is not null)
            {
                value = Bytes.FromHexString((string)response.Result);
                if (_recordDb is not null)
                {
                    _recordDb[key] = value;
                }
            }

            return value;
        }
    }
}

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Merge.AuRa
{
    public class AuRaMergeBlockProducerEnvFactory : BlockProducerEnvFactory
    {
        private readonly AuRaNethermindApi _auraApi;
        private readonly IAuraConfig _auraConfig;
        private readonly DisposableStack _disposeStack;

        public AuRaMergeBlockProducerEnvFactory(
            AuRaNethermindApi auraApi,
            IAuraConfig auraConfig,
            DisposableStack disposeStack,
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IReadOnlyTrieStore readOnlyTrieStore,
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptStorage,
            IBlockPreprocessorStep blockPreprocessorStep,
            ITxPool txPool,
            ITransactionComparerProvider transactionComparerProvider,
            IBlocksConfig blocksConfig,
            ILogManager logManager) : base(
                dbProvider,
                blockTree,
                readOnlyTrieStore,
                specProvider,
                blockValidator,
                rewardCalculatorSource,
                receiptStorage,
                blockPreprocessorStep,
                txPool,
                transactionComparerProvider,
                blocksConfig,
                logManager)
        {
            _auraApi = auraApi;
            _auraConfig = auraConfig;
            _disposeStack = disposeStack;
        }

        protected override BlockProcessor CreateBlockProcessor(
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptStorage,
            ILogManager logManager,
            IBlocksConfig blocksConfig)
        {
            return new AuRaMergeBlockProcessor(
                specProvider,
                blockValidator,
                rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                TransactionsExecutorFactory.Create(readOnlyTxProcessingEnv),
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                receiptStorage,
                logManager,
                _blockTree);
        }

        protected override TxPoolTxSource CreateTxPoolTxSource(
            ReadOnlyTxProcessingEnv processingEnv,
            ITxPool txPool,
            IBlocksConfig blocksConfig,
            ITransactionComparerProvider transactionComparerProvider,
            ILogManager logManager)
        {
            ReadOnlyTxProcessingEnv constantContractsProcessingEnv = CreateReadonlyTxProcessingEnv(
                _dbProvider.AsReadOnly(false),
                _blockTree.AsReadOnly());

            return new StartBlockProducerAuRa(_auraApi)
                .CreateTxPoolTxSource(processingEnv, constantContractsProcessingEnv);
        }
    }
}

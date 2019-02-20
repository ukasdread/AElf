using System.Collections.Generic;
using AElf.Common;
using AElf.Kernel;
using AElf.Kernel.Services;
using AElf.Kernel.Miner.Application;
using Volo.Abp.Threading;

namespace AElf.Kernel.Consensus.Application
{
    public class ConsensusTransactionGenerator : ISystemTransactionGenerator
    {
        private readonly IConsensusService _consensusService;

        public ConsensusTransactionGenerator(IConsensusService consensusService)
        {
            _consensusService = consensusService;
        }
        
        public void GenerateTransactions(Address from, ulong preBlockHeight, ulong refBlockHeight, byte[] refBlockPrefix,
            int chainId, ref List<Transaction> generatedTransactions)
        {
            generatedTransactions.AddRange(
                AsyncHelper.RunSync(() =>
                    _consensusService.GenerateConsensusTransactionsAsync(chainId, refBlockHeight, refBlockPrefix)));
        }
    }
}
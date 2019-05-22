using System;
using System.Linq;
using Acs4;
using AElf.Kernel;
using AElf.Sdk.CSharp;
using AElf.Types;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace AElf.Contracts.Consensus.AEDPoS
{
    public partial class AEDPoSContract
    {
        /// <summary>
        /// Get next consensus behaviour of the caller based on current state.
        /// This method can be tested by testing GetConsensusCommand.
        /// </summary>
        /// <param name="publicKey"></param>
        /// <param name="dateTime"></param>
        /// <param name="currentRound">Return current round information to avoid unnecessary database access.</param>
        /// <returns></returns>
        private AElfConsensusBehaviour GetBehaviour(string publicKey, DateTime dateTime, out Round currentRound)
        {
            currentRound = null;

            if (!TryToGetCurrentRoundInformation(out currentRound) ||
                !currentRound.RealTimeMinersInformation.ContainsKey(publicKey))
            {
                return AElfConsensusBehaviour.Nothing;
            }

            var ableToGetPreviousRound = TryToGetPreviousRoundInformation(out var previousRound);
            var isTermJustChanged = IsJustChangedTerm(out var termNumber);
            var isTimeSlotPassed = currentRound.IsTimeSlotPassed(publicKey, dateTime, out var minerInRound);
            if (minerInRound.OutValue == null)
            {
                // Current miner hasn't produce block in current round before.

                if (!ableToGetPreviousRound && minerInRound.Order != 1 &&
                    currentRound.RealTimeMinersInformation.Values.First(m => m.Order == 1).OutValue == null)
                {
                    // In first round, if block of boot node not executed, don't produce block to
                    // avoid forks creating.
                    return AElfConsensusBehaviour.NextRound;
                }

                if (!ableToGetPreviousRound || isTermJustChanged)
                {
                    // Failed to get previous round information or just changed term.
                    return AElfConsensusBehaviour.UpdateValueWithoutPreviousInValue;
                }

                if (currentRound.ExtraBlockProducerOfPreviousRound == publicKey &&
                    dateTime < currentRound.GetStartTime() &&
                    minerInRound.ProducedTinyBlocks < AEDPoSContractConstants.TinyBlocksNumber)
                {
                    return AElfConsensusBehaviour.TinyBlock;
                }

                if (!isTimeSlotPassed)
                {
                    // If this node not missed his time slot of current round.
                    return AElfConsensusBehaviour.UpdateValue;
                }
            }
            else if (minerInRound.ProducedTinyBlocks < AEDPoSContractConstants.TinyBlocksNumber)
            {
                return AElfConsensusBehaviour.TinyBlock;
            }
            else if (currentRound.ExtraBlockProducerOfPreviousRound == publicKey &&
                     minerInRound.ProducedTinyBlocks < AEDPoSContractConstants.TinyBlocksNumber.Mul(2))
            {
                return AElfConsensusBehaviour.TinyBlock;
            }

            // If this node missed his time slot, a command of terminating current round will be fired,
            // and the terminate time will based on the order of this node (to avoid conflicts).

            // Side chain will go next round directly.
            if (State.TimeEachTerm.Value == int.MaxValue)
            {
                return AElfConsensusBehaviour.NextRound;
            }

            // In first round, the blockchain start timestamp is incorrect.
            // We can return NextRound directly.
            if (currentRound.RoundNumber == 1)
            {
                return AElfConsensusBehaviour.NextRound;
            }

            Assert(TryToGetBlockchainStartTimestamp(out var blockchainStartTimestamp),
                "Failed to get blockchain start timestamp.");

            Context.LogDebug(() => $"Using start timestamp: {blockchainStartTimestamp}");

            // Calculate the approvals and make the judgement of changing term.
            var changeTerm =
                currentRound.IsTimeToChangeTerm(previousRound, blockchainStartTimestamp, termNumber,
                    State.TimeEachTerm.Value);
            return changeTerm
                ? AElfConsensusBehaviour.NextTerm
                : AElfConsensusBehaviour.NextRound;
        }

        private bool TryToGetBlockchainStartTimestamp(out Timestamp startTimestamp)
        {
            startTimestamp = State.BlockchainStartTimestamp.Value;
            return startTimestamp != null;
        }

        private bool IsJustChangedTerm(out long termNumber)
        {
            termNumber = 0;
            return TryToGetPreviousRoundInformation(out var previousRound) &&
                   TryToGetTermNumber(out termNumber) &&
                   previousRound.TermNumber != termNumber;
        }

        private bool TryToGetTermNumber(out long termNumber)
        {
            termNumber = State.CurrentTermNumber.Value;
            return termNumber != 0;
        }

        private bool TryToGetRoundNumber(out long roundNumber)
        {
            roundNumber = State.CurrentRoundNumber.Value;
            return roundNumber != 0;
        }

        private bool TryToGetCurrentRoundInformation(out Round roundInformation)
        {
            roundInformation = null;
            if (TryToGetRoundNumber(out var roundNumber))
            {
                roundInformation = State.Rounds[roundNumber];
                if (roundInformation != null)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryToGetPreviousRoundInformation(out Round previousRound)
        {
            previousRound = new Round();
            if (TryToGetRoundNumber(out var roundNumber))
            {
                if (roundNumber < 2)
                {
                    return false;
                }

                previousRound = State.Rounds[(roundNumber - 1)];
                return !previousRound.IsEmpty;
            }

            return false;
        }

        private bool TryToGetRoundInformation(long roundNumber, out Round roundInformation)
        {
            roundInformation = State.Rounds[roundNumber];
            return roundInformation != null;
        }

        private Transaction GenerateTransaction(string methodName, IMessage parameter)
        {
            var tx = new Transaction
            {
                From = Context.Sender,
                To = Context.Self,
                MethodName = methodName,
                Params = parameter.ToByteString()
            };

            return tx;
        }

        private int ConvertDurationToMilliseconds(Duration duration)
        {
            return (int) duration.Seconds.Mul(1000).Add(duration.Nanos.Div(1000000));
        }

        private int GetNextBlockMiningLeftMillisecondsForFirstRound(MinerInRound minerInRound, DateTime blockTime)
        {
            var actualMiningTime = minerInRound.ActualMiningTimes.First();
            var producedTinyBlocks = minerInRound.ProducedTinyBlocks;
            var timeForEachBlock = State.MiningInterval.Value.Div(AEDPoSContractConstants.TinyBlocksNumber);
            var expectedMiningTime = actualMiningTime.ToDateTime()
                .AddMilliseconds(timeForEachBlock.Mul(producedTinyBlocks)).ToTimestamp();
            var leftMilliseconds = ConvertDurationToMilliseconds(expectedMiningTime - blockTime.ToTimestamp());
            return leftMilliseconds;
        }

        private int GetNextBlockMiningLeftMillisecondsForPreviousRoundExtraBlockProducer(
            Timestamp previousExtraBlockTimestamp, int producedTinyBlocks, DateTime blockTime)
        {
            var timeForEachBlock = State.MiningInterval.Value.Div(AEDPoSContractConstants.TinyBlocksNumber);
            var expectedMiningTime = previousExtraBlockTimestamp.ToDateTime()
                .AddMilliseconds(timeForEachBlock.Mul(producedTinyBlocks)).ToTimestamp();
            var leftMilliseconds = ConvertDurationToMilliseconds(expectedMiningTime - blockTime.ToTimestamp());
            return leftMilliseconds;
        }

        private void SetBlockchainStartTimestamp(Timestamp timestamp)
        {
            Context.LogDebug(() => $"Set start timestamp to {timestamp}");
            State.BlockchainStartTimestamp.Value = timestamp;
        }

        private bool TryToUpdateRoundNumber(long roundNumber)
        {
            var oldRoundNumber = State.CurrentRoundNumber.Value;
            if (roundNumber != 1 && oldRoundNumber + 1 != roundNumber)
            {
                return false;
            }

            State.CurrentRoundNumber.Value = roundNumber;
            return true;
        }

        private bool TryToAddRoundInformation(Round round)
        {
            var ri = State.Rounds[round.RoundNumber];
            if (ri != null)
            {
                return false;
            }

            State.Rounds[round.RoundNumber] = round;
            return true;
        }

        private bool TryToUpdateRoundInformation(Round round)
        {
            var ri = State.Rounds[round.RoundNumber];
            if (ri == null)
            {
                return false;
            }

            State.Rounds[round.RoundNumber] = round;
            return true;
        }
    }
}
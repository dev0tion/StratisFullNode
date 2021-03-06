﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin.AsyncWork;
using Stratis.Bitcoin.Connection;
using Stratis.Bitcoin.Consensus;
using Stratis.Bitcoin.Consensus.Validators;
using Stratis.Bitcoin.Features.BlockStore.AddressIndexing;
using Stratis.Bitcoin.Features.Miner;
using Stratis.Bitcoin.Features.PoA;
using Stratis.Bitcoin.Features.PoA.Voting;
using Stratis.Bitcoin.Features.Wallet.Interfaces;
using Stratis.Bitcoin.Interfaces;
using Stratis.Bitcoin.Mining;
using Stratis.Bitcoin.Utilities;
using Stratis.Features.Collateral.CounterChain;

namespace Stratis.Features.Collateral
{
    /// <summary>
    /// Collateral aware version of <see cref="PoAMiner"/>. At the block template creation it will check our own collateral at a commitment height which is
    /// calculated in a following way: <c>counter chain height - maxReorgLength - AddressIndexer.SyncBuffer</c>. Then commitment height is encoded in
    /// OP_RETURN output of a coinbase transaction.
    /// </summary>
    public class CollateralPoAMiner : PoAMiner
    {
        private readonly CollateralHeightCommitmentEncoder encoder;

        private readonly ICollateralChecker collateralChecker;

        private readonly Network counterChainNetwork;

        public CollateralPoAMiner(IConsensusManager consensusManager, IDateTimeProvider dateTimeProvider, Network network, INodeLifetime nodeLifetime, ILoggerFactory loggerFactory,
            IInitialBlockDownloadState ibdState, BlockDefinition blockDefinition, ISlotsManager slotsManager, IConnectionManager connectionManager,
            PoABlockHeaderValidator poaHeaderValidator, IFederationManager federationManager, IIntegrityValidator integrityValidator, IWalletManager walletManager,
            INodeStats nodeStats, VotingManager votingManager, PoAMinerSettings poAMinerSettings, ICollateralChecker collateralChecker, IAsyncProvider asyncProvider, ICounterChainSettings counterChainSettings)
            : base(consensusManager, dateTimeProvider, network, nodeLifetime, loggerFactory, ibdState, blockDefinition, slotsManager, connectionManager,
            poaHeaderValidator, federationManager, integrityValidator, walletManager, nodeStats, votingManager, poAMinerSettings, asyncProvider)
        {
            this.counterChainNetwork = counterChainSettings.CounterChainNetwork;
            this.collateralChecker = collateralChecker;
            this.encoder = new CollateralHeightCommitmentEncoder(this.logger);
        }

        /// <inheritdoc />
        protected override void FillBlockTemplate(BlockTemplate blockTemplate, out bool dropTemplate)
        {
            base.FillBlockTemplate(blockTemplate, out dropTemplate);

            int counterChainHeight = this.collateralChecker.GetCounterChainConsensusHeight();
            int maxReorgLength = AddressIndexer.GetMaxReorgOrFallbackMaxReorg(this.network);

            int commitmentHeight = counterChainHeight - maxReorgLength - AddressIndexer.SyncBuffer;

            if (commitmentHeight <= 0)
            {
                dropTemplate = true;
                this.logger.LogWarning("Counter chain should first advance at least at {0}! Block can't be produced.", maxReorgLength + AddressIndexer.SyncBuffer);
                this.logger.LogTrace("(-)[LOW_COMMITMENT_HEIGHT]");
                return;
            }

            IFederationMember currentMember = this.federationManager.GetCurrentFederationMember();

            if (currentMember == null)
            {
                dropTemplate = true;
                this.logger.LogWarning("Unable to get this node's federation member!");
                this.logger.LogTrace("(-)[CANT_GET_FED_MEMBER]");
                return;
            }

            // Check our own collateral at a given commitment height.
            bool success = this.collateralChecker.CheckCollateral(currentMember, commitmentHeight);

            if (!success)
            {
                dropTemplate = true;
                this.logger.LogWarning("Failed to fulfill collateral requirement for mining!");
                this.logger.LogTrace("(-)[BAD_COLLATERAL]");
                return;
            }

            // Add height commitment.
            byte[] encodedHeight = this.encoder.EncodeCommitmentHeight(commitmentHeight);

            var heightCommitmentScript = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(encodedHeight), Op.GetPushOp(this.counterChainNetwork.MagicBytes));
            blockTemplate.Block.Transactions[0].AddOutput(Money.Zero, heightCommitmentScript);
        }
    }

    public sealed class CollateralHeightCommitmentEncoder
    {
        /// <summary>Prefix used to identify OP_RETURN output with mainchain consensus height commitment.</summary>
        public static readonly byte[] HeightCommitmentOutputPrefixBytes = { 121, 13, 6, 253 };

        private readonly ILogger logger;

        public CollateralHeightCommitmentEncoder(ILogger logger)
        {
            this.logger = logger;
        }

        /// <summary>Converts <paramref name="height"/> to a byte array which has a prefix of <see cref="HeightCommitmentOutputPrefixBytes"/>.</summary>
        /// <param name="height">That height at which the block was mined.</param>
        /// <returns>The encoded height in bytes.</returns>
        public byte[] EncodeCommitmentHeight(int height)
        {
            var bytes = new List<byte>(HeightCommitmentOutputPrefixBytes);

            bytes.AddRange(BitConverter.GetBytes(height));

            return bytes.ToArray();
        }

        /// <summary>Extracts the height commitment data from a transaction's coinbase <see cref="TxOut"/>.</summary>
        /// <param name="coinbaseTx">The transaction that should contain the height commitment data.</param>
        /// <returns>The commitment height, <c>null</c> if not found.</returns>
        public (int? height, uint? magic) DecodeCommitmentHeight(Transaction coinbaseTx)
        {
            IEnumerable<Script> opReturnOutputs = coinbaseTx.Outputs.Where(x => (x.ScriptPubKey.Length > 0) && (x.ScriptPubKey.ToBytes(true)[0] == (byte)OpcodeType.OP_RETURN)).Select(x => x.ScriptPubKey);

            byte[] commitmentData = null;
            byte[] magic = null;

            this.logger.LogDebug("Transaction contains {0} OP_RETURN outputs.", opReturnOutputs.Count());

            foreach (Script script in opReturnOutputs)
            {
                Op[] ops = script.ToOps().ToArray();

                if (ops.Length != 2 && ops.Length != 3)
                    continue;

                byte[] data = ops[1].PushData;

                bool correctPrefix = data.Take(HeightCommitmentOutputPrefixBytes.Length).SequenceEqual(HeightCommitmentOutputPrefixBytes);

                if (!correctPrefix)
                {
                    this.logger.LogDebug("Push data contains incorrect prefix for height commitment.");
                    continue;
                }

                commitmentData = data.Skip(HeightCommitmentOutputPrefixBytes.Length).ToArray();

                if (ops.Length == 3)
                    magic = ops[2].PushData;

                break;
            }

            if (commitmentData != null)
                return (BitConverter.ToInt32(commitmentData), ((magic == null) ? (uint?)null : BitConverter.ToUInt32(magic)));

            return (null, null);
        }
    }
}

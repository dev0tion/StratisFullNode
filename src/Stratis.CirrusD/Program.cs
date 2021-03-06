﻿using System;
using System.Threading.Tasks;
using NBitcoin.Protocol;
using Stratis.Bitcoin;
using Stratis.Bitcoin.Builder;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Features.Api;
using Stratis.Bitcoin.Features.BlockStore;
using Stratis.Bitcoin.Features.MemoryPool;
using Stratis.Bitcoin.Features.PoA;
using Stratis.Bitcoin.Features.RPC;
using Stratis.Bitcoin.Features.SignalR;
using Stratis.Bitcoin.Features.SignalR.Broadcasters;
using Stratis.Bitcoin.Features.SignalR.Events;
using Stratis.Bitcoin.Features.SmartContracts;
using Stratis.Bitcoin.Features.SmartContracts.PoA;
using Stratis.Bitcoin.Features.SmartContracts.Wallet;
using Stratis.Bitcoin.Utilities;
using Stratis.Features.Collateral;
using Stratis.Features.Diagnostic;
using Stratis.Features.SQLiteWalletRepository;
using Stratis.Sidechains.Networks;

namespace Stratis.CirrusD
{
    class Program
    {
        public static void Main(string[] args)
        {
            MainAsync(args).Wait();
        }

        public static async Task MainAsync(string[] args)
        {
            try
            {
                var nodeSettings = new NodeSettings(networksSelector: CirrusNetwork.NetworksSelector,
                    protocolVersion: ProtocolVersion.CIRRUS_VERSION, args: args)
                {
                    MinProtocolVersion = ProtocolVersion.ALT_PROTOCOL_VERSION
                };

                ((PoAConsensusOptions)nodeSettings.Network.Consensus.Options).AutoKickIdleMembers = false;

                IFullNode node = GetSideChainFullNode(nodeSettings);

                if (node != null)
                    await node.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("There was a problem initializing the node. Details: '{0}'", ex.Message);
            }
        }

        private static IFullNode GetSideChainFullNode(NodeSettings nodeSettings)
        {
            IFullNodeBuilder nodeBuilder = new FullNodeBuilder()
                .UseNodeSettings(nodeSettings)
                .UseBlockStore()
                .UseMempool()
                .AddSmartContracts(options =>
                {
                    options.UseReflectionExecutor();
                    options.UsePoAWhitelistedContracts();
                })
                .UseSmartContractPoAConsensus()
                .UseSmartContractPoAMining() // TODO: this needs to be refactored and removed as it does not make sense to call this for non-mining nodes.
                .CheckForPoAMembersCollateral(false) // This is a non-mining node so we will only check the commitment height data and not do the full set of collateral checks.
                .UseSmartContractWallet()
                .AddSQLiteWalletRepository()
                .UseApi()
                .AddRPC()
                .UseDiagnosticFeature();

            if (nodeSettings.EnableSignalR)
            {
                nodeBuilder.AddSignalR(options =>
                {
                    options.EventsToHandle = new[]
                    {
                        (IClientEvent) new BlockConnectedClientEvent(),
                        new TransactionReceivedClientEvent()
                    };

                    options.ClientEventBroadcasters = new[]
                    {
                        (Broadcaster: typeof(CirrusWalletInfoBroadcaster),
                            ClientEventBroadcasterSettings: new ClientEventBroadcasterSettings
                            {
                                BroadcastFrequencySeconds = 5
                            })
                    };
                });
            }

            return nodeBuilder.Build();
        }
    }
}
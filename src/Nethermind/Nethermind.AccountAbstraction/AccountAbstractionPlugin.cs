﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Timers;
using Nethermind.Evm.Tracing.Access;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction
{
    public class AccountAbstractionPlugin : IConsensusWrapperPlugin
    {
        private IAccountAbstractionConfig _accountAbstractionConfig = null!;
        private UserOperationPool? _userOperationPool;
        private UserOperationSimulator? _userOperationSimulator;
        private AccessBlockTracer _accessBlockTracer = null!;
        private IDictionary<Address, int> _paymasterOffenseCounter = new Dictionary<Address, int>();
        private ISet<Address> _bannedPaymasters = new HashSet<Address>();
        private Address _singletonContractAddress = null!;
        
        private INethermindApi _nethermindApi = null!;
        private ILogger _logger;

        private TimerFactory _timerFactory = new TimerFactory();

        public string Name => "Account Abstraction";

        public string Description => "Implements account abstraction via alternative mempool (ERC-4337)";

        public string Author => "Nethermind";

        private UserOperationPool UserOperationPool
        {
            get
            {
                if (_userOperationPool is null)
                {
                    var (getFromApi, _) = _nethermindApi!.ForProducer;
                    UserOperationSortedPool userOperationSortedPool = new(
                        _accountAbstractionConfig.UserOperationPoolSize,
                        CompareUserOperationsByDecreasingGasPrice.Default,
                        getFromApi.LogManager);

                    _userOperationPool = new UserOperationPool(
                        _nethermindApi.BlockTree,
                        _nethermindApi.StateProvider,
                        _nethermindApi.Timestamper,
                        AccessBlockTracer,
                        _accountAbstractionConfig,
                        _paymasterOffenseCounter,
                        _bannedPaymasters,
                        _nethermindApi.PeerManager,
                        userOperationSortedPool,
                        UserOperationSimulator
                    );
                }

                return _userOperationPool;
            }
        }

        private UserOperationSimulator UserOperationSimulator
        {
            get
            {
                if (_userOperationSimulator is null)
                {
                    var (getFromApi, _) = _nethermindApi!.ForProducer;

                    _userOperationSimulator = new(
                        getFromApi.StateProvider,
                        getFromApi.EngineSigner,
                        _accountAbstractionConfig,
                        _singletonContractAddress,
                        getFromApi.SpecProvider,
                        getFromApi.BlockTree,
                        getFromApi.DbProvider,
                        getFromApi.ReadOnlyTrieStore,
                        getFromApi.LogManager,
                        getFromApi.BlockPreprocessor);
                }

                return _userOperationSimulator;
            }
        }
        
        private AccessBlockTracer AccessBlockTracer
        {
            get
            {
                if (_accessBlockTracer is null)
                {
                    var (getFromApi, _) = _nethermindApi!.ForProducer;

                    _accessBlockTracer = new AccessBlockTracer(Array.Empty<Address>());
                }

                return _accessBlockTracer;
            }
        }

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
            _accountAbstractionConfig = _nethermindApi.Config<IAccountAbstractionConfig>();
            _logger = _nethermindApi.LogManager.GetClassLogger();

            if (_accountAbstractionConfig.Enabled)
            {
                bool parsed = Address.TryParse(_accountAbstractionConfig.SingletonContractAddress, out Address _singletonContractAddress);
                if (!parsed) _logger.Error("Account Abstraction Plugin: Singleton contract address could not be parsed");
            }

            if (Enabled)
            {
                if (_logger.IsInfo) _logger.Info("Account Abstraction Plugin: User Operation Mining Enabled");
            }
            else
            {
                if (_logger.IsInfo) _logger.Info("Account Abstraction Plugin: User Operation Mining Disabled");
            }

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol() => Task.CompletedTask;

        public Task InitRpcModules()
        {
            if (Enabled)
            {
                (IApiWithNetwork getFromApi, _) = _nethermindApi!.ForRpc;

                IJsonRpcConfig rpcConfig = getFromApi.Config<IJsonRpcConfig>();
                rpcConfig.EnableModules(ModuleType.AccountAbstraction);

                AccountAbstractionModuleFactory accountAbstractionModuleFactory = new(
                    UserOperationPool);

                getFromApi.RpcModuleProvider!.RegisterBoundedByCpuCount(accountAbstractionModuleFactory,
                    rpcConfig.Timeout);

                if (_logger!.IsInfo) _logger.Info("Account Abstraction RPC plugin enabled");
            }
            else
            {
                if (_logger!.IsWarn) _logger.Info("Skipping Account Abstraction RPC plugin");
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task<IBlockProducer> InitBlockProducer(IConsensusPlugin consensusPlugin)
        {
            if (!Enabled)
            {
                throw new InvalidOperationException("Account Abstraction plugin is disabled");
            }

            UInt256 minerBalance = _nethermindApi.StateProvider!.GetBalance(_nethermindApi.EngineSigner!.Address);
            if (minerBalance < 1.Ether())
            {
                _logger.Warn($"Account Abstraction Plugin: Miner ({_nethermindApi.EngineSigner!.Address}) Ether balance low - {minerBalance/1.Ether()} Ether < 1 Ether. Increasing balance is recommended");
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"Account Abstraction Plugin: Miner ({_nethermindApi.EngineSigner!.Address}) Ether balance adequate - {minerBalance/1.Ether()} Ether");
            }

            IManualBlockProductionTrigger trigger = new BuildBlocksWhenRequested();
            UserOperationTxSource userOperationTxSource = new(UserOperationPool, UserOperationSimulator, _logger);
            ContinuousBundleSender continuousBundleSender = new ContinuousBundleSender(_nethermindApi.BlockTree, userOperationTxSource, _accountAbstractionConfig, _timerFactory);
            return consensusPlugin.InitBlockProducer(trigger, userOperationTxSource);
        }

        public bool Enabled => _nethermindApi.Config<IInitConfig>().IsMining && _accountAbstractionConfig.Enabled;
    }
}

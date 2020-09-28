﻿using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Vault;
using Nethermind.Vault.Config;
using Nethermind.Vault.JsonRpc;

namespace Nethermind.Plugin.Baseline
{
    public class VaultPlugin : INethermindPlugin
    {
        private INethermindApi _api;

        private ILogger _logger;

        private IVaultConfig _vaultConfig;
        private VaultService _vaultService;

        public void Dispose() { }

        public string Name => "Vault";

        public string Description => "Provide Vault Connector";

        public string Author => "Nethermind";

        public Task Init(INethermindApi api)
        {
            _vaultConfig = api.Config<IVaultConfig>();
            _api = api;
            _logger = api.LogManager.GetClassLogger();
            _vaultService = new VaultService(_vaultConfig, _api.LogManager);
            
            IVaultWallet wallet = new VaultWallet(_vaultService, _vaultConfig.VaultId, _api.LogManager);
            ITxSigner vaultSigner = new VaultTxSigner(wallet, _api.ChainSpec.ChainId);
            
            // TODO: change vault to provide, use sealer to set the gas price as well
            // TODO: need to verify the timing of initializations so the TxSender replacement works fine
            _api.TxSender = new VaultTxSender(vaultSigner, _vaultConfig, _api.ChainSpec.ChainId);
            
            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (_vaultConfig.Enabled)
            {
                VaultModule vaultModule = new VaultModule(_vaultService, _api.LogManager);
                _api.RpcModuleProvider!.Register(new SingletonModulePool<IVaultModule>(vaultModule, true));
                if (_logger.IsInfo) _logger.Info("Vault RPC Module has been enabled");
            }
            
            return Task.CompletedTask;
        }
    }
}
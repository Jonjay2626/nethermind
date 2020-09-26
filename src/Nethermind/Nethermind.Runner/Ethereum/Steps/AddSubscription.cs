﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Analytics;
using Nethermind.Logging;
using Nethermind.PubSub;
using Nethermind.Runner.Analytics;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeBlockchain), typeof(StartGrpcProducer), typeof(StartKafkaProducer), typeof(StartLogProducer))]
    public class AddSubscriptions : IStep
    {
        private readonly INethermindApi _api;
        private ILogger _logger;

        public AddSubscriptions(INethermindApi api)
        {
            _api = api;
            _logger = api.LogManager.GetClassLogger();
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            IAnalyticsConfig analyticsConfig = _api.Config<IAnalyticsConfig>();
            InitBlocksStreaming(analyticsConfig);
            InitTransactionStreaming(analyticsConfig);
            LoadPlugins(analyticsConfig);
            return Task.CompletedTask;
        }

        private void InitBlocksStreaming(IAnalyticsConfig analyticsConfig)
        {
            if (analyticsConfig.StreamBlocks)
            {
                BlocksSubscription subscription =
                    new BlocksSubscription(_api.Producers, _api.MainBlockProcessor, _api.LogManager);
                _api.DisposeStack.Push(subscription);
            }
        }

        private void InitTransactionStreaming(IAnalyticsConfig analyticsConfig)
        {
            if (analyticsConfig.StreamTransactions)
            {
                TransactionsSubscription subscription =
                    new TransactionsSubscription(_api.Producers, _api.MainBlockProcessor, _api.LogManager);
                _api.DisposeStack.Push(subscription);
            }
        }

        private void LoadPlugins(IAnalyticsConfig analyticsConfig)
        {
            if (analyticsConfig.PluginsEnabled)
            {
                IInitConfig initConfig = _api.Config<IInitConfig>();
                string fullPluginsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, initConfig.PluginsDirectory);
                if (!Directory.Exists(fullPluginsDir))
                {
                    if (_logger.IsWarn) _logger.Warn($"Plugins folder {fullPluginsDir} was not found. Skipping.");
                    return;
                }
            
                string[] pluginFiles = Directory.GetFiles(fullPluginsDir).Where(p => p.EndsWith("dll")).ToArray();
                if (pluginFiles.Length > 0)
                {
                    if (_logger.IsInfo) _logger.Info($"Loading {pluginFiles.Length} analytics plugins from {fullPluginsDir}");
                }

                foreach (string path in pluginFiles)
                {
                    if (_logger.IsInfo) _logger.Warn($"Loading assembly {path}");
                    Assembly assembly = Assembly.LoadFile(Path.Combine(fullPluginsDir, path));
                    foreach (Type type in assembly.GetExportedTypes())
                    {
                        if (typeof(IAnalyticsPluginLoader).IsAssignableFrom(type))
                        {
                            InitPlugin(_logger, type);
                        }
                    }
                }
            }
        }

        private void InitPlugin(ILogger logger, Type type)
        {
            if (logger.IsInfo) logger.Info($"Activating pluging {type.Name}");
            IAnalyticsPluginLoader? pluginLoader = Activator.CreateInstance(type) as IAnalyticsPluginLoader;
            foreach (IProducer producer in _api.Producers)
            {
                var bridge = new TxPublisherBridge(producer);
                pluginLoader?.Init(_api.FileSystem, _api.TxPool, _api.BlockTree, _api.MainBlockProcessor, bridge, _api.LogManager);
            }
        }

        private class TxPublisherBridge : IDataPublisher, IProducer
        {
            private readonly IProducer _producer;

            public TxPublisherBridge(IProducer producer)
            {
                _producer = producer ?? throw new ArgumentNullException(nameof(producer));
            }

            public Task PublishAsync<T>(T data) where T : class
            {
                return _producer.PublishAsync(data);
            }
        }
    }
}
﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.RoutingTable;

public class NodeTable : INodeTable
{
    private readonly ILogger _logger;
    private readonly INetworkConfig _networkConfig;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly INodeDistanceCalculator _nodeDistanceCalculator;

    public NodeTable(
        INodeDistanceCalculator? nodeDistanceCalculator,
        IDiscoveryConfig? discoveryConfig,
        INetworkConfig? networkConfig,
        ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
        _nodeDistanceCalculator = nodeDistanceCalculator ?? throw new ArgumentNullException(nameof(nodeDistanceCalculator));
            
        Buckets = new NodeBucket[_discoveryConfig.BucketsCount];
        for (int i = 0; i < Buckets.Length; i++)
        {
            Buckets[i] = new NodeBucket(i, _discoveryConfig.BucketSize);
        }
    }

    public Node? MasterNode { get; private set; }    
        
    public NodeBucket[] Buckets { get; }

    public NodeAddResult AddNode(Node node)
    {
        CheckInitialization();
            
        if (_logger.IsTrace) _logger.Trace($"Adding node to NodeTable: {node}");
        int distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode!.IdHash.Bytes, node.IdHash.Bytes);
        NodeBucket bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
        return bucket.AddNode(node);
    }

    public void ReplaceNode(Node nodeToRemove, Node nodeToAdd)
    {
        CheckInitialization();
            
        int distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode!.IdHash.Bytes, nodeToAdd.IdHash.Bytes);
        NodeBucket bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
        bucket.ReplaceNode(nodeToRemove, nodeToAdd);
    }

    private void CheckInitialization()
    {
        if (MasterNode == null)
        {
            throw new InvalidOperationException("Master not has not been initialized");
        }
    }

    public void RefreshNode(Node node)
    {
        CheckInitialization();
            
        int distanceFromMaster = _nodeDistanceCalculator.CalculateDistance(MasterNode!.IdHash.Bytes, node.IdHash.Bytes);
        NodeBucket bucket = Buckets[distanceFromMaster > 0 ? distanceFromMaster - 1 : 0];
        bucket.RefreshNode(node);
    }

    public IEnumerable<Node> GetClosestNodes()
    {
        int count = 0;
        int bucketSize = _discoveryConfig.BucketSize;
            
        foreach (NodeBucket nodeBucket in Buckets)
        {
            foreach (NodeBucketItem nodeBucketItem in nodeBucket.BondedItems)
            {
                if (count < bucketSize)
                {
                    count++;
                    if (nodeBucketItem.Node is not null)
                    {
                        yield return nodeBucketItem.Node;
                    }
                }
                else
                {
                    yield break;
                }
            }
        }
    }

    public IEnumerable<Node> GetClosestNodes(byte[] nodeId)
    {
        CheckInitialization();
            
        Keccak idHash = Keccak.Compute(nodeId);
        return Buckets.SelectMany(x => x.BondedItems)
            .Where(x => x.Node?.IdHash != idHash && x.Node is not null)
            .Select(x => new {x.Node, Distance = _nodeDistanceCalculator.CalculateDistance(x.Node!.Id.Bytes, nodeId)})
            .OrderBy(x => x.Distance)
            .Take(_discoveryConfig.BucketSize)
            .Select(x => x.Node!);
    }

    public void Initialize(PublicKey masterNodeKey)
    {
        MasterNode = new Node(masterNodeKey, _networkConfig.ExternalIp, _networkConfig.DiscoveryPort);
        if (_logger.IsTrace) _logger.Trace($"Created MasterNode: {MasterNode}");
    }
}

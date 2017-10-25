﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Hosting;
using Orleans;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.Runtime.Configuration;

namespace Derivco.Orniscient.Viewer.Clients
{
    public static class GrainClientMultiton
    {
        private static readonly Dictionary<string, IClusterClient> _clients = new Dictionary<string, IClusterClient>();

        private static readonly SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1);
        private static readonly object _lock = new object();

        public static async Task<IClusterClient> GetClient(string key)
        {
            await _semaphoreSlim.WaitAsync();
            try
            {
                if (!_clients[key].IsInitialized)
                {
                    await _clients[key].Connect();
                }
            }
            finally
            {
                _semaphoreSlim.Release();

            }
            return _clients[key];
        }

        public static string RegisterClient(string address, int port)
        {
            var grainClientKey = Guid.NewGuid().ToString();
            lock (_lock)
            {
                _clients.Add(grainClientKey,
                    new ClientBuilder().UseConfiguration(GetConfiguration(address, port)).Build());
            }
            return grainClientKey;
        }

        public static void RemoveClient(string key)
        {
            lock (_lock)
            {
                if (_clients.ContainsKey(key))
                {
                    var client = _clients[key];
                    _clients.Remove(key);
                    client.Dispose();
                }
            }
        }

        private static ClientConfiguration GetConfiguration(string address, int port)
        {
            var host = Dns.GetHostEntry(address);
            var ipAddress = host.AddressList.Last();
            var ipEndpoint = new IPEndPoint(ipAddress, port);

            var configuration =
                new ClientConfiguration
                {
                    GatewayProvider = ClientConfiguration.GatewayProviderType.Config
                };
            configuration.Gateways.Add(ipEndpoint);
            configuration.RegisterStreamProvider<SimpleMessageStreamProvider>("OrniscientSMSProvider");

            return configuration;
        }
    }
}
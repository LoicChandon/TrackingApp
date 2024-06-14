using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using NidRpc;
using NurApiDotNet;

namespace TrackingApp
{
    public class Application
    {
        private readonly Plugin _rpc;
        private readonly NurApi _nur;
        private readonly ManualResetEventSlim _backGroundResetEvent = new ManualResetEventSlim();
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1);
        private bool _connected = false;
        private string _connectError = null;
        private NurApi.ReaderInfo? _readerInfo = null;
        private bool _streamEnabled = false;
        private DateTime? _lastStreamEvent = null;
        private uint _nStreamEvents = 0;
        private readonly Dictionary<byte[], TagEntry> _tagsSeen = new Dictionary<byte[], TagEntry>(new ByteArrayComparer());

        public static async Task<Application> CreateInstanceAsync(string appName)
        {
            var rpc = new Plugin("application", appName);
            var application = new Application(rpc);
            await rpc.ConnectAsync();
            return application;
        }

        public Application(Plugin rpc)
        {
            _rpc = rpc;
            _nur = new NurApi();

            // Initialize NurApi events
            _nur.ConnectedEvent += NurConnectedEvent;
            _nur.DisconnectedEvent += NurDisconnectedEvent;
            _nur.InventoryStreamEvent += OnInventoryStreamEvent;

            // Set up RPC callbacks
            SetupRpcCallbacks();
        }

        private void SetupRpcCallbacks()
        {
            _rpc["/rfid/connected"].CallbackReceived += RfidConnected;
            _rpc["/rfid/connect"].CallbackReceived += RfidConnect;
            _rpc["/rfid/disconnect"].CallbackReceived += RfidDisconnect;
            _rpc["/tags/startStream"].CallbackReceived += TagsStartStream;
            _rpc["/tags/stopStream"].CallbackReceived += TagsStopStream;
            _rpc["/inventory/get"].CallbackReceived += InventoryGet;
        }

        public void Run()
        {
            BackgroundConnect();
            while (true)
            {
                _backGroundResetEvent.Wait();
            }
        }

        // Implement RPC callback methods here...

        private void NurConnectedEvent(object sender, NurApi.NurEventArgs e)
        {
            // Handle NurApi connected event
        }

        private void NurDisconnectedEvent(object sender, NurApi.NurEventArgs e)
        {
            // Handle NurApi disconnected event
        }

        private void OnInventoryStreamEvent(object sender, NurApi.InventoryStreamEventArgs ev)
        {
            // Handle NurApi inventory stream event
        }

        private void BackgroundConnect()
        {
            // Implement background connection logic
        }

        private void StartTagStream()
        {
            // Implement tag stream start logic
        }

        // Other methods...

        private class TagEntry
        {
            public string epc;
            public byte antennaId;
            public DateTime timeserie;
        }

        private class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] x, byte[] y)
            {
                if (x == null || y == null)
                    return x == y;
                
                return x.SequenceEqual(y);
            }

            public int GetHashCode(byte[] obj)
            {
                if (obj == null)
                    return 0;
                
                return obj.Sum(b => b);
            }
        }
    }
}

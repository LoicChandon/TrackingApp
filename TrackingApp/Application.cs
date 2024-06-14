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

        #region NUR_CALLBACKS

        private void NurConnectedEvent(object sender, NurApi.NurEventArgs e)
        {
            _lock.Wait();
            _connected = true;
            _lock.Release();

            var thread = new Thread(() =>
            {
                NurApi.ReaderInfo? readerInfo = null;
                try
                {
                    readerInfo = _nur.GetReaderInfo();
                }
                catch (NurApiException ex)
                {
                    Console.WriteLine($"Failed to get reader info {ex.Message}");
                }
                _lock.Wait();
                _readerInfo = readerInfo;
                _lock.Release();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void NurDisconnectedEvent(object sender, NurApi.NurEventArgs e)
        {
            _lock.Wait();
            try
            {
                _connected = false;
                _readerInfo = null;
                _streamEnabled = false;
                _tagsSeen.Clear();
                _nStreamEvents = 0;
            }
            finally
            {
                _lock.Release();
            }
        }

        private void OnInventoryStreamEvent(object sender, NurApi.InventoryStreamEventArgs ev)
        {
            NurApi.TagStorage nurStorage = _nur.GetTagStorage();
            _lock.Wait();
            try
            {
                if (!_streamEnabled)
                {
                    return;
                }
                // need to lock access to the tag storage object to
                // prevent NurApi from updating it in the background
                lock (nurStorage)
                {
                    foreach (NurApi.Tag tag in nurStorage)
                    {
                        if (_tagsSeen.TryGetValue(tag.epc, out TagEntry value))
                        {
                            value.antennaId = tag.antennaId;
                            value.timeserie = DateTime.Now; //Update timeserie to get the most recent one
                        }
                        else
                        {
                            _tagsSeen[tag.epc] = new TagEntry()
                            {
                                epc = BitConverter.ToString(tag.epc).Replace("-", ""),
                                antennaId = tag.antennaId,
                                timeserie = DateTime.Now, // Initialize timeserie
                            };
                        }
                    }
                    // Clear NurApi internal tag storage so that we only get new tags next next time
                    nurStorage.Clear();
                }
                // NurApi may disable the stream to prevent unnecessarily powering the radio
                // (in case the application has stopped); start it again if that is the case
                if (_streamEnabled && ev.data.stopped)
                {
                    StartTagStream();
                }
                _lastStreamEvent = DateTime.Now;
                _nStreamEvents++;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                _lock.Release();
            }
        }

        #endregion

        #region RPC_CALLBACKS
        private async Task<JObject> RfidConnected(object sender, CallbackEventArgs args)
        {
            await _lock.WaitAsync();
            var connected = _connected ? "true" : "false";
            var connectError = _connectError;
            _lock.Release();

            var ret = JObject.Parse($"{{'connected': {connected}}}");
            if (connectError != null)
            {
                ret["connectError"] = connectError;
            }
            return ret;
        }
        private void BackgroundConnect()
        {
            var thread = new Thread(() =>
            {
                try
                {
                    _nur.ConnectSocket("127.0.0.1", 4332); //localhost + TCP port 4332
                    _lock.Wait();
                    _connectError = null;
                    _lock.Release();
                }
                catch (NurApiException ex)
                {
                    _lock.Wait();
                    _connectError = ex.Message;
                    _lock.Release();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        private async Task<JObject> RfidConnect(object sender, CallbackEventArgs args)
        {
            BackgroundConnect();
            return await Task.FromResult(JObject.Parse("{}"));
        }

        private async Task<JObject> RfidDisconnect(object sender, CallbackEventArgs args)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    _nur.Disconnect();
                }
                catch (NurApiException ex)
                {
                    _lock.Wait();
                    _connectError = ex.Message;
                    _lock.Release();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await Task.FromResult(JObject.Parse("{}"));
        }


        private async Task<JObject> TagsStartStream(object sender, CallbackEventArgs args)
        {
            await _lock.WaitAsync();
            try
            {
                _streamEnabled = true;
                _tagsSeen.Clear();
                _nStreamEvents = 0;
            }
            finally
            {
                _lock.Release();
            }
            var thread = new Thread(() =>
            {
                try
                {
                    _nur.ClearTagsEx();
                    StartTagStream();
                }
                catch (NurApiException ex)
                {
                    Console.WriteLine($"Failed to start tag reading {ex.Message}");
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await Task.FromResult(JObject.Parse("{}"));
        }
        private async Task<JObject> TagsStopStream(object sender, CallbackEventArgs args)
        {
            await _lock.WaitAsync();
            _streamEnabled = false;
            _lock.Release();
            var thread = new Thread(() =>
            {
                try
                {
                    _nur.StopInventoryStream();
                }
                catch (NurApiException ex)
                {
                    Console.WriteLine($"Failed to stop tag reading {ex.Message}");
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await Task.FromResult(JObject.Parse("{}"));
        }

        private async Task<JObject> InventoryGet(object sender, CallbackEventArgs args)
        {
            int count;
            uint nStreamEvents;
            string streamEnabled = _streamEnabled ? "true" : "false";
            var tags = new List<dynamic>(); //We use dynamic type to serialize timeseries under the correct format
            await _lock.WaitAsync();
            try
            {
                count = _tagsSeen.Count;
                nStreamEvents = _nStreamEvents;
                foreach (TagEntry tagEntry in _tagsSeen.Values)
                {
                    tags.Add(tagEntry);
                }
            }
            finally
            {
                _lock.Release();
            }

            var jsonTxt = $"{{'count': {count}, 'nInventories': {nStreamEvents}, 'updateEnabled': {streamEnabled}, 'tags': {JsonConvert.SerializeObject(tags)}}}";
            var ret = await Task.FromResult(JObject.Parse(jsonTxt));
            if (_lastStreamEvent.HasValue)
            {
                ret["timestamp"] = _lastStreamEvent.Value.ToString("yyyy-MM-dd HH\\:mm\\:ss");
            }
            return ret;
        }

        #endregion

        private void StartTagStream()
        {
            // TODO: fix when NUR_OPFLAGS_EN_PHASE_DIFF/NUR_DC_PHASEDIFF has been added to NurApiDotNet
            // tag phase diff support (NUR_OPFLAGS_EN_PHASE_DIFF = (1 << 17)) isn't yet available in
            // NurApiDotNet; just assume it is supported in the NUR module and turn it on
            _nur.OpFlags |= (1 << 17);
            _nur.StartInventoryStream();
        }

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

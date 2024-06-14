using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using NidRpc;
using NurApiDotNet;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;




namespace TrackingApp
{
    //---------------------------------------------------------------------Rebuild API---------------------------------------------------------------------------
    //If you want to change the release version, update it before in the "manifest.json" file.
    //To update this API, you first need to rebuild the solution (you can change to the "release" build [optionnal]).
    //then, copy all the files in the bin\Debug (or bin\Release if you used the "release" build) directory to the ZipContents\bin directory.
    //Now, sign the application by opening powershell on this project (right click on "TrackingApp", then "open in terminal".
    //Execute the supplied fr_appsigntool.exe :
    // ..\fr22_appsigntool\fr_appsigntool.exe ZipContents
    //Connect to the NordicId reader (https://10.11.92.136) --> user : admin / password written at the back of the reader.
    //On the left side, "Software" --> "Applications" --> Browse the zip file you just created (same directory as the project) and click "Install".
    //Good job !!
    //-----------------------------------------------------------------------------------------------------------------------------------------------------------

    class Application
    {
        //API in C# : link between the FR22 Reader and the application

        #region CORS
        public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureServices(services =>
                {
                    services.AddCors(options =>
                    {
                        options.AddPolicy("AllowAllOrigins",
                            builder =>
                            {
                                builder.AllowAnyOrigin()
                                       .AllowAnyMethod()
                                       .AllowAnyHeader();
                            });
                    });

                    services.AddControllers();
                });

                webBuilder.Configure(app =>
                {
                    var env = app.ApplicationServices.GetRequiredService<IHostEnvironment>();

                    if (env.IsDevelopment())
                    {
                        app.UseDeveloperExceptionPage();
                    }

                    app.UseCors("AllowAllOrigins");

                    app.UseRouting();

                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();
                    });
                });
            });


        #endregion

        class TagEntry
        {
            public string epc;
            public byte antennaId;
            public DateTime timeserie;
        }
        public class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] x, byte[] y)
            {
                if (x == null || y == null)
                {
                    return x == y;
                }
                return x.SequenceEqual(y);
            }

            public int GetHashCode(byte[] obj)
            {
                if (obj == null)
                {
                    return 0;
                }
                return obj.Sum(b => b);
            }
        }
        readonly Plugin _rpc;
        readonly NurApi _nur;
        readonly ManualResetEventSlim _backGroundResetEvent = new ManualResetEventSlim();

        readonly SemaphoreSlim _lock = new SemaphoreSlim(1);
        bool _connected = false;
        string _connectError = null;
        NurApi.ReaderInfo? _readerInfo = null;
        bool _streamEnabled = false;
        DateTime? _lastStreamEvent = null;
        uint _nStreamEvents = 0;
        readonly Dictionary<byte[], TagEntry> _tagsSeen = new Dictionary<byte[], TagEntry>(new ByteArrayComparer());

        async public static Task<Application> CreateInstanceAsync(string appName)
        {
            var rpc = new Plugin("application", appName);
            var appplication = new Application(rpc);
            await rpc.ConnectAsync();
            return appplication;
        }

        #region Router
        Application(Plugin rpc)
        {
            try
            {
                _nur = new NurApi();
                _nur.ConnectedEvent += NurConnectedEvent;
                _nur.DisconnectedEvent += NurDisconnectedEvent;
                _nur.InventoryStreamEvent += new EventHandler<NurApi.InventoryStreamEventArgs>(OnInventoryStreamEvent);
            }
            catch (NurApiException ex)
            {
                Console.WriteLine(ex.Message);
                throw ex;
            }

            //router
            _rpc = rpc;
            _rpc["/rfid/connected"].CallbackReceived += RfidConnected;
            _rpc["/rfid/connect"].CallbackReceived += RfidConnect;
            _rpc["/rfid/disconnect"].CallbackReceived += RfidDisconnect;
            //_rpc["/rfid/readerinfo"].CallbackReceived += RfidReaderInfo;

            _rpc["/tags/startStream"].CallbackReceived += TagsStartStream;
            _rpc["/tags/stopStream"].CallbackReceived += TagsStopStream;

            _rpc["/inventory/get"].CallbackReceived += InventoryGet;
        }

        #endregion

        public void Run()
        {
            BackgroundConnect();
            while (true)
            {
                _backGroundResetEvent.Wait();
            }
        }


        #region RPC_CALLBACKS
        async Task<JObject> RfidConnected(object sender, CallbackEventArgs args)
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

        void BackgroundConnect()
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
        async Task<JObject> RfidConnect(object sender, CallbackEventArgs args)
        {
            BackgroundConnect();
            return await Task.FromResult(JObject.Parse("{}"));
        }

        async Task<JObject> RfidDisconnect(object sender, CallbackEventArgs args)
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

        #region NUR_CALLBACKS

        void NurConnectedEvent(object sender, NurApi.NurEventArgs e)
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
        void NurDisconnectedEvent(object sender, NurApi.NurEventArgs e)
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

        void OnInventoryStreamEvent(object sender, NurApi.InventoryStreamEventArgs ev)
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

        void StartTagStream()
        {
            // TODO: fix when NUR_OPFLAGS_EN_PHASE_DIFF/NUR_DC_PHASEDIFF has been added to NurApiDotNet
            // tag phase diff support (NUR_OPFLAGS_EN_PHASE_DIFF = (1 << 17)) isn't yet available in
            // NurApiDotNet; just assume it is supported in the NUR module and turn it on
            _nur.OpFlags |= (1 << 17);
            _nur.StartInventoryStream();
        }

        class Program
        {
            //static async Task Main()
            //{
            //    var application = await Application.CreateInstanceAsync("TrackingApp");
            //    application.Run();
            //}
            static async Task Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();

            var application = await Application.CreateInstanceAsync("TrackingApp");
            var runTask = Task.Run(() => application.Run());

            await host.RunAsync();
            await runTask;
        }
        }
    }
}

// MainPage.cs (Updated)
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;



namespace MauiApp1
{
    public partial class MainPage : ContentPage, INotifyPropertyChanged
    {
        private BoxView heartbeatIndicator;
        private GraphicsView? graphView;
        private ObservableCollection<string> logLines = new();
        private CollectionView logView;
        private List<(DateTime time, double pwm, double rpm)> telemetryBuffer = new();
        private const int MaxBufferSeconds = 10;
        private const int MaxRPM = 7000;

        private readonly Queue<string> logQueue = new();
        private readonly object logLock = new();
        private UdpClient udpClient;
        private const int listenPort = 6789;
        private byte[] telPrefix = Encoding.ASCII.GetBytes("[TEL]");
        private byte[] hbtPrefix = Encoding.ASCII.GetBytes("[HBT]");
        private byte[] hbcPrefix = Encoding.ASCII.GetBytes("[HBC]");
        private DateTime lastHeartbeat = DateTime.MinValue;
        private bool isConnected => (DateTime.UtcNow - lastHeartbeat).TotalSeconds < 3;
        private bool carDisconnected = false;

        private CancellationTokenSource? udpListenerCts;
        //private List<(DateTime time, double pwm, double rpm)> telemetryBuffer = new();
        private const int ReverseLimitRPM = 700; // 10% limit
        private const int MaxPitch = 45; // degrees
        private const int MaxAccelZ = 250; // in m/s² * 100
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        private BoxView? directionIndicator;
        private TelemetryLogger logger = new();
        private bool isLogging = true;
        private bool isLiveMode = true;
        private Label logLabel;
        private StringBuilder logBuilder = new StringBuilder();
        public TelemetryPacket lastpacket = new TelemetryPacket();

        private Label distanceLabel; // Added distance label
        public string DistanceDisplay => $"Distance: {_distance} mm";


        //private DateTime lastHeartbeat = DateTime.MinValue;
        ///private bool isConnected => (DateTime.UtcNow - lastHeartbeat).TotalSeconds < 3;
        private int _distance;
        public int Distance
        {
            get => _distance;
            set
            {
                if (_distance != value)
                {
                    _distance = value;
                    OnPropertyChanged(nameof(Distance));
                }
            }
        }
        protected override void OnAppearing()
        {
            base.OnAppearing();

            if (udpClient == null)
            {
                udpClient = new UdpClient(listenPort);
                StartUDPListener();  // ✅ Only start the listener once
                Log("✅ UDP Listener started.");
            }

            SendCommand("[CMD]CONFIG:SEND");  // Safe to request config after UI is up
        }

        private bool isUserAtBottom = true;
        public MainPage()
        {
            SetupGraphUI();
            logger.StartLogging();

            const int MaxFlushPerFrame = 5;
            const int MaxQueueSize = 500;
            Dispatcher.StartTimer(TimeSpan.FromMilliseconds(100), () =>
            {
                if (udpListenerCts?.IsCancellationRequested == true || this.Handler == null)
                {
                    return true;
                }

                List<string> messagesToFlush = new();
                if (!isLiveMode)
                {
                    SimulateTelemetry();
                }

                lock (logLock)
                {
                    while (logQueue.Count > MaxQueueSize)
                    {
                        logQueue.Dequeue();
                    }

                    int linesFlushed = 0;
                    while (logQueue.Count > 0 && linesFlushed < MaxFlushPerFrame)
                    {
                        messagesToFlush.Add(logQueue.Dequeue());
                        linesFlushed++;
                    }
                }

                if (this.Handler == null)
                {
                    return true;
                }

                foreach (var line in messagesToFlush)
                {
                    if (logLines.Count >= 100)
                        logLines.RemoveAt(0);
                    logLines.Add(line);
                }

                if (messagesToFlush.Count > 0 && logView != null && logLines.Count > 0)
                {
                    if (logView != null && logLines.Count > 0)
                    {
                        var lastItem = logLines.Last();
                        var index = logLines.IndexOf(lastItem);

                        if (index >= logLines.Count - 5) // Near end? Then scroll
                        {

                            if (messagesToFlush.Count > 0 && logView != null && logLines.Count > 0)
                            {
                                if (isUserAtBottom)
                                {
                                    logView.ScrollTo(logLines.Last(), position: ScrollToPosition.End, animate: false);
                                }
                            }

                            //logView.ScrollTo(lastItem, position: ScrollToPosition.End, animate: false);
                        }

                    }
                    //logView.ScrollTo(logLines.Last(), position: ScrollToPosition.End, animate: false);
                }

                graphView?.Invalidate();

                heartbeatIndicator.BackgroundColor =
                    !isConnected ? Colors.DarkGray :
                    carDisconnected ? Colors.Orange :
                    Colors.LimeGreen;

                return true;
            });

        }

        private bool HasMeaningfulChange(TelemetryPacket a, TelemetryPacket b)
        {
            return Math.Abs(a.accelZ - b.accelZ) > 3 ||
                   Math.Abs(a.distance - b.distance) > 5 ||
                   Math.Abs(a.rpm - b.rpm) > 10 ||
                   Math.Abs(a.pwm - b.pwm) > 5 ||
                   Encoding.ASCII.GetString(a.evt).TrimEnd('\0') != Encoding.ASCII.GetString(b.evt).TrimEnd('\0');
}
        public void OnNewTelemetry(TelemetryPacket packet)
        {
            double pwmPct =-1* ((packet.pwm - 1500) / 500.0);
            double rpmPct = packet.rpm == 65535 ? 0 : packet.rpm / (double)MaxRPM;
            rpmPct = pwmPct < 0 ? -Math.Abs(rpmPct) : Math.Abs(rpmPct);

            Distance = packet.distance;
            OnPropertyChanged(nameof(Distance));
            OnPropertyChanged(nameof(DistanceDisplay));
            if (packet.pwm !=1500 || packet.rpm != 0)
            {
                if (isLogging && HasMeaningfulChange(lastpacket, packet))
                {
                    lastpacket = packet;
                    logger.LogPacket(packet, null);
                }

                Log($"PWM: {packet.pwm}, RPM: {packet.rpm}, Distance: {packet.distance}");

                if ((packet.pwm != 1500 || packet.rpm > 0) && !double.IsNaN(pwmPct) && !double.IsNaN(rpmPct))
                {
                    telemetryBuffer.Add((DateTime.UtcNow, pwmPct, rpmPct));
                    telemetryBuffer.RemoveAll(e => (DateTime.UtcNow - e.time).TotalSeconds > MaxBufferSeconds);
                }

                if (directionIndicator != null)
                {
                    directionIndicator.BackgroundColor =
                        pwmPct < 0 ? Colors.Red :
                        pwmPct > 0 ? Colors.Green : Colors.Gray;
                }

                // 🆕 Update distance label
                if (distanceLabel != null)
                {
                    //  if (packet.distance < 500)
                    //{
                  //  int x = 0;


                    //distanceLabel.Text
                   // Distance = packet.distance;
                   // distanceLabel.Text=$"Distance: {packet.distance} mm";

                                        distanceLabel.TextColor = packet.distance < 300 ? Colors.Red : Colors.White;
                    // }
                }

            }


            //if (isLogging && !lastpacket.Equals(packet))
            //{
            //    lastpacket = packet;
            //    logger.LogPacket(packet, null);
            //}
            //if (isLogging && !IsPacketSimilar(packet, lastpacket))
           
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            udpListenerCts?.Cancel();
            udpListenerCts?.Dispose();
            udpListenerCts = null;
            logger.StopLogging();
        }
        private void StartUDPListener()
        {
            udpListenerCts = new CancellationTokenSource();
            var token = udpListenerCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var result = await udpClient.ReceiveAsync();
                        var data = result.Buffer;

                        //try
                        //{
                        //    var rawString = Encoding.ASCII.GetString(data);
                        //    Log($"📥 Raw UDP from {result.RemoteEndPoint}: \"{rawString}\"");
                        //}
                        //catch
                        //{
                        //    Log($"📥 Raw UDP (non-text): {BitConverter.ToString(data)}");
                        //}

                        ProcessReceivedData(data);
                    }
                }
                catch (ObjectDisposedException)
                {
                    Log("UDP listener stopped cleanly.");
                }
                catch (Exception ex)
                {
                    Log($"❌ UDP listener error: {ex.GetType().Name} - {ex.Message}");
                }
            }, token);
        }
        private string knownMiddlemanIP;
        private void ProcessReceivedData(byte[] data)
        {
            if (data.Length >= 5 && data.Take(5).SequenceEqual(telPrefix))
            {
                int size = Marshal.SizeOf<TelemetryPacket>();
                if (data.Length >= 5 + size)
                {
                    var payload = data.Skip(5).Take(size).ToArray();
                    if (payload.Length == size)
                    {
                        try
                        {
                            var packet = ByteArrayToStruct<TelemetryPacket>(payload);
                            OnNewTelemetry(packet);
                            carDisconnected = false;
                        }
                        catch (Exception ex)
                        {
                            Log($"❌ Failed to decode telemetry: {ex.Message}");
                        }
                    }
                    else
                    {
                        Log($"⚠️ Payload length mismatch. Expected {size}, got {payload.Length}");
                    }
                }
                else
                {
                    Log($"⚠️ Packet too short. Got {data.Length}, expected at least {5 + size}");
                }
            }

            else if (data.Length >= 7 && Encoding.ASCII.GetString(data).StartsWith("[HELLO]"))
            {
                string ipString = Encoding.ASCII.GetString(data).Substring(7).Trim();
                Log($"📬 Middleman reported IP: {ipString}");
                knownMiddlemanIP = ipString;
            }
            else if (data.Length >= 5 && data.Take(5).SequenceEqual(hbtPrefix))
            {
                lastHeartbeat = DateTime.UtcNow;
                Log("📶 Middleman alive");
            }
            else if (data.Length >= 5 && data.Take(5).SequenceEqual(hbcPrefix))
            {
                lastHeartbeat = DateTime.UtcNow;
                carDisconnected = true;
                Log("❌ Car connection lost");
            }
            else if (data.Length >= 5 && Encoding.ASCII.GetString(data).StartsWith("[CFG]"))
            {
                var payload = Encoding.ASCII.GetString(data).Substring(5).Trim();
                ParseConfigPayload(payload);
            }
        }
        private Switch indoorSwitch;
        private Slider reverseLimitSlider;
        private Slider wheelieSuppressSlider;
        private void ParseConfigPayload(string payload)
        {
            var parts = payload.Split('|');
            foreach (var part in parts)
            {
                if (part.StartsWith("REV:"))
                    reverseLimitSlider.Value = double.Parse(part.Substring(4));
                else if (part.StartsWith("WHEELIE:"))
                    wheelieSuppressSlider.Value = double.Parse(part.Substring(8));
                else if (part.StartsWith("INDOOR:"))
                    indoorSwitch.IsToggled = part.Substring(7) == "1";
            }
            Log($"🟢 Config synced: {payload}");
        }

        private static T ByteArrayToStruct<T>(byte[] bytes) where T : struct
        {

            if (bytes.Length != Marshal.SizeOf<T>())
                throw new ArgumentException($"Expected {Marshal.SizeOf<T>()} bytes, got {bytes.Length}");


            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        private void Log(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            lock (logLock)
            {
                logQueue.Enqueue(line);
                if (logQueue.Count > 200)
                    logQueue.Dequeue();
            }
        }
        private Label ipLabel;
        private Button beepButton;

        private void SetupGraphUI()
        {
            distanceLabel = new Label
            {
                Text = "Distance: ---",
                TextColor = Colors.White,
                FontSize = 16
            };
            //distanceLabel.SetBinding(Label.TextProperty, nameof(Distance));
            distanceLabel.SetBinding(Label.TextProperty, nameof(DistanceDisplay));
            BindingContext = this;

            var simulateButton = new Button
            {
                Text = "🔄 Toggle Simulation",
                BackgroundColor = Colors.Teal,
                TextColor = Colors.White
            };

            simulateButton.Clicked += (s, e) =>
            {
                isLiveMode = !isLiveMode;
                simulateButton.Text = isLiveMode ? "🔴 Switch to Simulation" : "🟢 Switch to Live";
                Log(isLiveMode ? "🟢 Live mode ENABLED (listening to car)" : "🟠 Simulation mode ENABLED");
            };
            ipLabel = new Label
            {
                Text = "📱 Dashboard IP: (detecting...)",
                TextColor = Colors.White,
                FontSize = 14
            };

            beepButton = new Button
            {
                Text = "🔊 Beep",
                BackgroundColor = Colors.DarkSlateBlue,
                TextColor = Colors.White
            };
            beepButton.Clicked += (s, e) => SendCommand("[CMD]TONE:WARNING");

            logView = new CollectionView
            {
                HeightRequest = 150,
                ItemTemplate = new DataTemplate(() =>
                {
                    var label = new Label
                    {
                        FontSize = 12,
                        TextColor = Colors.White
                    };
                    label.SetBinding(Label.TextProperty, ".");
                    return label;
                })
            };
            logView.ItemsSource = logLines;

            directionIndicator = new BoxView
            {
                WidthRequest = 20,
                HeightRequest = 20,
                CornerRadius = 10,
                BackgroundColor = Colors.Gray
            };

            heartbeatIndicator = new BoxView
            {
                WidthRequest = 20,
                HeightRequest = 20,
                CornerRadius = 10,
                BackgroundColor = Colors.DarkGray
            };

            graphView = new GraphicsView
            {
                Drawable = new TelemetryGraphDrawable(() => telemetryBuffer),
                HeightRequest = 300
            };

            var layout = new VerticalStackLayout
            {
                Padding = 10,
                Spacing = 10,
                Children =
        {
            new Label { Text = "Direction", FontAttributes = FontAttributes.Bold },
            directionIndicator,
            new Label { Text = "Heartbeat", FontAttributes = FontAttributes.Bold },

                   distanceLabel,
            heartbeatIndicator,
            new Label { Text = "PWM vs RPM (Normalized, Center = Neutral)" },
            graphView,
            new Label { Text = "Log Output", FontAttributes = FontAttributes.Bold },
            logView,
            new Label { Text = "Car Command", FontAttributes = FontAttributes.Bold },
            beepButton,
            ipLabel
        }
            };
            layout.Children.Add(simulateButton);

            // Configuration Section Header
            var configHeader = new Label
            {
                Text = "⚙️ Car Configuration",
                FontSize = 18,
                FontAttributes = FontAttributes.Bold,
                TextColor = Colors.White
            };

            // Indoor Mode Switch
             indoorSwitch = new Switch();
            var indoorLabel = new Label
            {
                Text = "Indoor Mode (limits forward power)",
                VerticalOptions = LayoutOptions.Center,
                TextColor = Colors.White
            };

            indoorSwitch.Toggled += (s, e) =>
            {
                SendCommand(e.Value ? "[CMD]CONFIG:INDOOR:ON" : "[CMD]CONFIG:INDOOR:OFF");
            };

            // Reverse Limit Slider
             reverseLimitSlider = new Slider(0, 1, 0.1);
            var reverseLimitLabel = new Label
            {
                Text = "Reverse Limit: 10%",
                TextColor = Colors.White
            };

            reverseLimitSlider.ValueChanged += (s, e) =>
            {
                reverseLimitLabel.Text = $"Reverse Limit: {e.NewValue:P0}";
                SendCommand($"[CMD]CONFIG:REV_LIMIT:{e.NewValue:F2}");
            };

            // Wheelie Suppression Slider
             wheelieSuppressSlider = new Slider(0, 1, 0.3);
            var wheelieSuppressLabel = new Label
            {
                Text = "Wheelie Suppression: 30%",
                TextColor = Colors.White
            };

            wheelieSuppressSlider.ValueChanged += (s, e) =>
            {
                wheelieSuppressLabel.Text = $"Wheelie Suppression: {e.NewValue:P0}";
                SendCommand($"[CMD]CONFIG:WHEELIE_SUPPRESS:{e.NewValue:F2}");
            };

            // Config Buttons (2-column layout)
            var printConfigButton = new Button
            {
                Text = "📋 Print Config",
                BackgroundColor = Colors.DarkSlateBlue,
                TextColor = Colors.White
            };
            printConfigButton.Clicked += (s, e) => SendCommand("[CMD]CONFIG:PRINT");

            var resetConfigButton = new Button
            {
                Text = "♻️ Reset to Defaults",
                BackgroundColor = Colors.DarkRed,
                TextColor = Colors.White
            };
            resetConfigButton.Clicked += (s, e) => SendCommand("[CMD]CONFIG:RESET");

            var liveModeButton = new Button
            {
                Text = "🟢 Enable Live Mode",
                BackgroundColor = Colors.Green,
                TextColor = Colors.White
            };
            liveModeButton.Clicked += (s, e) =>
            {
                isLiveMode = true;
                Log("🟢 Live mode ENABLED.");
            };

            var simulateModeButton = new Button
            {
                Text = "🟠 Enable Simulation",
                BackgroundColor = Colors.Orange,
                TextColor = Colors.White
            };
            simulateModeButton.Clicked += (s, e) =>
            {
                isLiveMode = false;
                Log("🟠 Simulation mode ENABLED.");
            };

            var buttonGrid = new Grid
            {
                ColumnDefinitions =
    {
        new ColumnDefinition { Width = GridLength.Star },
        new ColumnDefinition { Width = GridLength.Star }
    },
                RowDefinitions =
    {
        new RowDefinition { Height = GridLength.Auto },
        new RowDefinition { Height = GridLength.Auto }
    }
            };

            buttonGrid.Add(printConfigButton, 0, 0);
            buttonGrid.Add(resetConfigButton, 1, 0);
            buttonGrid.Add(liveModeButton, 0, 1);
            buttonGrid.Add(simulateModeButton, 1, 1);

            // Final config stack
            var configStack = new VerticalStackLayout
            {
                Padding = 10,
                Spacing = 10,
                Children =
    {
        configHeader,
        new HorizontalStackLayout { Children = { indoorLabel, indoorSwitch } },
        reverseLimitLabel, reverseLimitSlider,
        wheelieSuppressLabel, wheelieSuppressSlider,
        buttonGrid
    }
            };

            layout.Children.Add(configStack);  // ✅ Add to your existing layout!

            Content = new ScrollView { Content = layout };

            // Optional: Print out available network interfaces for IP detection
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = ni.GetIPProperties();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                    {
                        Log($"🌐 Interface: {ni.Name} | IP: {ua.Address}");
                    }
                }
            }
        }
        private void SendCommand(string cmd)
        {
            if (!string.IsNullOrWhiteSpace(knownMiddlemanIP))
            {
                try
                {
                    using var udp = new UdpClient();
                    udp.EnableBroadcast = true;

                    var bytes = Encoding.ASCII.GetBytes(cmd);
                    Log($"📡 Sending: {cmd} to {knownMiddlemanIP}:6789");

                    udp.Send(bytes, bytes.Length, knownMiddlemanIP, 6789);
                    Log("✅ Command sent");
                }
                catch (Exception ex)
                {
                    Log($"❌ Send error: {ex.Message}");
                }
            }
            else
            {
                Log("⚠️ No known middleman IP — cannot send command.");
            }

        }

        private bool IsPacketSimilar(TelemetryPacket a, TelemetryPacket b)
        {
            return a.pwm == b.pwm &&
                   a.rpm == b.rpm &&
                   a.pitch == b.pitch &&
                   a.distance == b.distance;
        }
        private void SimulateTelemetry()
        {
            var rnd = new Random();
            var packet = new TelemetryPacket
            {
                pwm = (ushort)rnd.Next(1000, 2000),
                rpm = (ushort)rnd.Next(0, 6000),
                pitch = (short)rnd.Next(-30, 30),
                roll = (short)rnd.Next(-20, 20),
                yaw = (short)rnd.Next(-180, 180),
                accelX = (short)rnd.Next(-200, 200),
                accelY = (short)rnd.Next(-200, 200),
                accelZ = (short)rnd.Next(100, 400) // add some realism
                , distance = (ushort)rnd.Next(100, 400),
                evt = new byte [3]

            };

            OnNewTelemetry(packet);
        }
    }

}

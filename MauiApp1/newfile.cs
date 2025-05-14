using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace MauiApp1
{
    public partial class MainPage : ContentPage
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
        private const int ReverseLimitRPM = 700;
        private const int MaxPitch = 45;
        private const int MaxAccelZ = 250;

        private BoxView? directionIndicator;
        private TelemetryLogger logger = new();
        private bool isLogging = true;
        private bool isLiveMode = true;
        private Label logLabel;
        private StringBuilder logBuilder = new StringBuilder();
        public TelemetryPacket lastpacket = new TelemetryPacket();
        private Label distanceLabel;
        private Label ipLabel;
        private Button beepButton;

        public MainPage()
        {
            InitializeComponent();
            SetupGraphUI();
            logger.StartLogging();
            StartUDPListener();
            Log("? MainPage initialized");
        }

        private void SetupGraphUI()
        {
            distanceLabel = new Label
            {
                Text = "Distance: ---",
                TextColor = Colors.White,
                FontSize = 16
            };

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

            var layout = new VerticalStackLayout
            {
                Padding = 10,
                Spacing = 10,
                Children =
                {
                    new Label { Text = "Direction", FontAttributes = FontAttributes.Bold },
                    directionIndicator,
                    new Label { Text = "Heartbeat", FontAttributes = FontAttributes.Bold },
                    heartbeatIndicator,
                    distanceLabel,
                    new Label { Text = "PWM vs RPM (Normalized, Center = Neutral)" },
                    graphView,
                    new Label { Text = "Log Output", FontAttributes = FontAttributes.Bold },
                    logView
                }
            };

            var reverseLimitSlider = new Slider(0, 1, 0.1);
var reverseLimitLabel = new Label { Text = "Reverse Limit: 10%", TextColor = Colors.White };
reverseLimitSlider.ValueChanged += (s, e) => {
    reverseLimitLabel.Text = $"Reverse Limit: {e.NewValue:P0}";
    SendCommand($"[CMD]CONFIG:REV_LIMIT:{e.NewValue:F2}");
};

var wheelieSuppressSlider = new Slider(0, 1, 0.3);
var wheelieSuppressLabel = new Label { Text = "Wheelie Suppression: 30%", TextColor = Colors.White };
wheelieSuppressSlider.ValueChanged += (s, e) => {
    wheelieSuppressLabel.Text = $"Wheelie Suppression: {e.NewValue:P0}";
    SendCommand($"[CMD]CONFIG:WHEELIE_SUPPRESS:{e.NewValue:F2}");
};

var indoorSwitch = new Switch();
var indoorLabel = new Label { Text = "Indoor Mode (limits forward power)", TextColor = Colors.White };
indoorSwitch.Toggled += (s, e) => {
    SendCommand(e.Value ? "[CMD]CONFIG:INDOOR:ON" : "[CMD]CONFIG:INDOOR:OFF");
};

layout.Children.Add(new Label { Text = "Configuration", FontAttributes = FontAttributes.Bold });
layout.Children.Add(reverseLimitLabel);
layout.Children.Add(reverseLimitSlider);
layout.Children.Add(wheelieSuppressLabel);
layout.Children.Add(wheelieSuppressSlider);
layout.Children.Add(new HorizontalStackLayout { Children = { indoorLabel, indoorSwitch } });

var printConfigButton = new Button
{
    Text = "?  Print Config",
    BackgroundColor = Colors.DarkSlateBlue,
    TextColor = Colors.White
};
printConfigButton.Clicked += (s, e) => SendCommand("[CMD]CONFIG:PRINT");

var resetConfigButton = new Button
{
    Text = "?? Reset to Defaults",
    BackgroundColor = Colors.DarkRed,
    TextColor = Colors.White
};
resetConfigButton.Clicked += (s, e) => SendCommand("[CMD]CONFIG:RESET");

var beepButton = new Button
{
    Text = "?  Beep",
    BackgroundColor = Colors.Indigo,
    TextColor = Colors.White
};
beepButton.Clicked += (s, e) => SendCommand("[CMD]TONE:WARNING");

layout.Children.Add(printConfigButton);
layout.Children.Add(resetConfigButton);
layout.Children.Add(beepButton);

var configFrame = new Frame
{
    BorderColor = Colors.Gray,
    Padding = 10,
    Margin = new Thickness(0, 20, 0, 20),
    BackgroundColor = Colors.Black,
    Content = new VerticalStackLayout
    {
        Spacing = 10,
        Children =
        {
            new Label { Text = "Configuration", FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
            reverseLimitLabel,
            reverseLimitSlider,
            wheelieSuppressLabel,
            wheelieSuppressSlider,
            new HorizontalStackLayout { Children = { indoorLabel, indoorSwitch } },
            printConfigButton,
            resetConfigButton,
            beepButton
        }
    }
};

layout.Children.Add(configFrame);

Content = new ScrollView { Content = layout };
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

        private void StartUDPListener()
        {
            udpClient = new UdpClient(listenPort);
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

                        try
                        {
                            var rawString = Encoding.ASCII.GetString(data);
                            Log($"?  Raw UDP from {result.RemoteEndPoint}: \"{rawString}\"");
                        }
                        catch
                        {
                            Log($"?  Raw UDP (non-text): {BitConverter.ToString(data)}");
                        }

                        ProcessReceivedData(data);
                    }
                }
                catch (ObjectDisposedException)
                {
                    Log("UDP listener stopped cleanly.");
                }
                catch (Exception ex)
                {
                    Log($"? UDP listener error: {ex.GetType().Name} - {ex.Message}");
                }
            }, token);
        }

        private static T ByteArrayToStruct<T>(byte[] bytes) where T : struct
        {
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

        private void ProcessReceivedData(byte[] data)
        {
            if (data.Length >= 5 && data.Take(5).SequenceEqual(telPrefix))
            {
                int size = Marshal.SizeOf<TelemetryPacket>();
                if (data.Length >= 5 + size)
                {
                    var packet = ByteArrayToStruct<TelemetryPacket>(data.Skip(5).Take(size).ToArray());
                    OnNewTelemetry(packet);
                    carDisconnected = false;
                }
            }
            else if (data.Length >= 7 && Encoding.ASCII.GetString(data).StartsWith("[HELLO]"))
            {
                string ipString = Encoding.ASCII.GetString(data).Substring(7).Trim();
                Log($"?  Middleman reported IP: {ipString}");
                knownMiddlemanIP = ipString;
            }
            else if (data.Length >= 5 && data.Take(5).SequenceEqual(hbtPrefix))
            {
                lastHeartbeat = DateTime.UtcNow;
                Log("?  Middleman alive");
            }
            else if (data.Length >= 5 && data.Take(5).SequenceEqual(hbcPrefix))
            {
                lastHeartbeat = DateTime.UtcNow;
                carDisconnected = true;
                Log("? Car connection lost");
            }
        }

        private void OnNewTelemetry(TelemetryPacket packet)
        {
            double pwmPct = (packet.pwm - 1500) / 500.0;
            double rpmPct = packet.rpm == 65535 ? 0 : packet.rpm / (double)MaxRPM;
            rpmPct = pwmPct < 0 ? -Math.Abs(rpmPct) : Math.Abs(rpmPct);

            if (isLogging && !lastpacket.Equals(packet))
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

            if (distanceLabel != null)
            {
                distanceLabel.Text = $"Distance: {packet.distance} mm";
                distanceLabel.TextColor = packet.distance < 300 ? Colors.Red : Colors.White;
            }
        }

        private string knownMiddlemanIP;
    }
}

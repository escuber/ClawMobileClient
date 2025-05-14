// TelemetryLogger.cs
using System;
using System.IO;
using System.Text;

namespace MauiApp1
{
    public class TelemetryLogger
    {
        private StreamWriter? writer;
        private string? currentFile;

        public void StartLogging()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClawLogs");
            Directory.CreateDirectory(folder);
            currentFile = Path.Combine(folder, $"telemetry_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            writer = new StreamWriter(currentFile, false, Encoding.UTF8);

            writer.WriteLine("timestamp,pwm,pwmpct,rpm,rpmpct,pitch,roll,yaw,accelX,accelY,accelZ,dist,event");
        }

        public void StopLogging()
        {
            writer?.Close();
            writer = null;
        }

        public void LogPacket(TelemetryPacket packet, string? evt = null)
        {
            if (writer == null) return;


            double pwmPct = -1 * ((packet.pwm - 1500) / 500.0);
            double rpmPct = packet.rpm == 65535 ? 0 : packet.rpm / 7000;
            rpmPct = pwmPct < 0 ? -Math.Abs(rpmPct) : Math.Abs(rpmPct);



            var line = string.Join(",",
                DateTime.UtcNow.ToString("o"),
                packet.pwm,
                pwmPct,// (packet.pwm - 1500) / 500.0,
            packet.rpm== 65535?0: packet.rpm,
            rpmPct,


//(packet.rpm == 65535)
//    ? 0
//    : packet.rpm / 7000.0,
            packet.pitch,
                packet.roll,
                packet.yaw,
                packet.accelX,
                packet.accelY,
                packet.accelZ,
                     packet.distance,

        evt ?? ""
            );

            writer.WriteLine(line);
            writer.Flush();
        }
    }

    // Integration notes for MainPage.cs:
    // 
    // private TelemetryLogger logger = new();
    // logger.StartLogging();
    // logger.LogPacket(packet, optionalEvent);
    // logger.StopLogging();
    // 
    // Example event triggers:
    // if (isReverseLimited) logger.LogPacket(packet, "REV_LIMITED");
    // if (isWheeliePrevented) logger.LogPacket(packet, "WHEELIE_PREVENTION");
}

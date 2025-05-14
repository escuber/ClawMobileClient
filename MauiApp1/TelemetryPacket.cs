using System;
using System.Collections.Generic;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using System.Timers;
using System.Runtime.InteropServices;

namespace MauiApp1
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TelemetryPacket
    {
        public ushort pwm;
        public short pitch;
        public short roll;
        public short yaw;
        public ushort rpm;
        public short accelX;
        public short accelY;
        public short accelZ;
        public ushort distance;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public byte[] evt;
    }
}
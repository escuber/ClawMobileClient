using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MauiApp1
{
    public class TelemetryGraphDrawable : IDrawable
    {
        private readonly Func<List<(DateTime time, double pwm, double rpm)>> _getData;

        public TelemetryGraphDrawable(Func<List<(DateTime time, double pwm, double rpm)>> getData)
        {
            _getData = getData;
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            
            if (_getData == null)
                return;
            var raw = _getData();
            if (raw == null) return;

            //var data = _getData().ToList(); ;
            List<(DateTime time, double pwm, double rpm)> data;
            try
            {
                data = _getData()?.ToList() ?? new();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Draw crash blocked: {ex.Message}");
                return;
            }

            if (data == null || data.Count == 0)
                return;

            //var data = _getData().ToList();
            //if (data.Count == 0) return;
            

            DateTime start = data[0].time;
            float width = dirtyRect.Width;
            float height = dirtyRect.Height;
            float centerY = height / 2f;

            PointF? lastPWM = null;
            PointF? lastRPM = null;

            // Draw center line (neutral)
            canvas.StrokeColor = Colors.DarkGray;
            canvas.StrokeSize = 1;
            canvas.DrawLine(0, centerY, width, centerY);

            // Draw scale labels
            canvas.FontColor = Colors.Gray;
            canvas.DrawString("1.0", new RectF(0, 0, 50, 20), HorizontalAlignment.Left, VerticalAlignment.Top);
            canvas.DrawString("0.0", new RectF(0, centerY - 10, 50, 20), HorizontalAlignment.Left, VerticalAlignment.Top);
            canvas.DrawString("-1.0", new RectF(0, height - 20, 50, 20), HorizontalAlignment.Left, VerticalAlignment.Top);
            
            foreach (var point in data)
            {
                float x = (float)((point.time - start).TotalSeconds / 10.0 * width);
                float pwmY = centerY - (float)(point.pwm * centerY);
                float rpmY = centerY - (float)(point.rpm * centerY);

                if (lastPWM is not null)
                {
                    canvas.StrokeColor = Colors.White;
                    canvas.DrawLine(lastPWM.Value.X, lastPWM.Value.Y, x, pwmY);
                }

                if (lastRPM is not null)
                {
                    canvas.StrokeColor = Colors.Lime;
                    canvas.DrawLine(lastRPM.Value.X, lastRPM.Value.Y, x, rpmY);
                }

                lastPWM = new PointF(x, pwmY);
                lastRPM = new PointF(x, rpmY);
            }
        }
    }
}

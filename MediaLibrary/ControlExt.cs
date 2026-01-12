using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using System;

#nullable enable

namespace CinecorePlayer2025
{
    internal static class ControlExt
    {
        public static T WithMargin<T>(this T c, int l, int t, int r, int b) where T : Control
        {
            c.Margin = new Padding(l, t, r, b);
            return c;
        }
    }
}

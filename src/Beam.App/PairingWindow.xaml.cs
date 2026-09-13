using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace Beam.App;

public partial class PairingWindow : Window
{
    public PairingWindow(string hostname, int port, string token)
    {
        InitializeComponent();

        // Encodes a real, openable URL (the PWA's setup page) rather than a
        // custom scheme — iOS has no handler registered for "beam://", so a
        // QR code pointing at one would fail to open when scanned.
        var payload = $"http://{hostname}:{port}/app/";
        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(qrData);
        var pngBytes = qrCode.GetGraphic(10);

        var bitmap = new BitmapImage();
        using (var stream = new System.IO.MemoryStream(pngBytes))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }

        QrImage.Source = bitmap;
        TokenText.Text = $"Host: {hostname}   Port: {port}   Token: {token}";
    }
}

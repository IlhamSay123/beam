using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace Beam.App;

public partial class PairingWindow : Window
{
    private readonly string _token;
    private readonly string _pairingLink;
    public PairingWindow(string hostname, int port, string token)
    {
        InitializeComponent();

        // Encodes a real, openable URL (the PWA's setup page) rather than a
        // custom scheme — iOS has no handler registered for "beam://", so a
        // QR code pointing at one would fail to open when scanned. The token
        // rides along as a query param (same pattern Jupyter uses) so
        // scanning the QR alone pairs the phone — no manual transcription of
        // an ambiguous alphanumeric string (which OCR/Live Text regularly
        // misreads, e.g. I/l/1, O/0, S/5).
        var payload = $"http://{hostname}:{port}/app/?token={Uri.EscapeDataString(token)}";
        _token = token;
        _pairingLink = payload;
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
        TokenText.Text = token;
        HostText.Text = $"http://{hostname}:{port}/app/";
    }
    private void CopyToken_Click(object sender, RoutedEventArgs e) => Copy(_token);
    private void CopyLink_Click(object sender, RoutedEventArgs e) => Copy(_pairingLink);
    private static void Copy(string value)
    {
        try { Clipboard.SetText(value); }
        catch (System.Runtime.InteropServices.COMException) { MessageBox.Show("The clipboard is busy. Try again.", "Beam"); }
    }
}

using QRCoder;
using SteamForge.Engine.Steam;

namespace OmniPacker.EngineHost.Engine;

/// <summary>
/// Dev-only affordance: when OMNIPACKER_ENGINE_TTY_QR is set, render each QR
/// challenge to a PNG in the temp dir and print its path to stderr, so a human
/// running the daemon in a terminal can open the image and scan it with the
/// Steam mobile app. The real Tauri host instead renders the QR from the
/// `auth.status` event's URL in its webview; this never touches the stdout
/// protocol stream.
/// </summary>
public static class QrTty
{
    public const string EnvFlag = "OMNIPACKER_ENGINE_TTY_QR";

    public static void AttachIfEnabled(EngineServices engine)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvFlag)))
            return;

        string? last = null;
        engine.Session.StatusChanged += status =>
        {
            if (status.State != SteamAuthState.AwaitingQrScan || string.IsNullOrEmpty(status.QrChallengeUrl))
                return;
            if (status.QrChallengeUrl == last)
                return;
            last = status.QrChallengeUrl;

            try
            {
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(status.QrChallengeUrl, QRCodeGenerator.ECCLevel.M);
                var png = new PngByteQRCode(data).GetGraphic(10);
                var path = Path.Combine(Path.GetTempPath(), "omnipacker-qr.png");
                File.WriteAllBytes(path, png);
                Console.Error.WriteLine($"[QR] Scan this image with the Steam mobile app: {path}");
                Console.Error.WriteLine($"[QR] URL: {status.QrChallengeUrl}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[QR] render failed: {ex.Message}");
            }
        };
    }
}

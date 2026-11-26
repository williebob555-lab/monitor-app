using MonitorApp.Client.Core;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Replication;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace MonitorApp.Client.Mobile;

// Face-to-face "Add a phone or tablet" (T085) on the phone's in-process source: the runtime does the cryptography and
// trust, this only draws the codes, opens the scanner and turns failures into messages the screen can show.
public sealed partial class InProcessPeerDataSource
{
    public string ThisDeviceVerificationCode => runtime.VerificationCode;

    public MeshCodeDto CreateJoinCode()
    {
        var code = runtime.CreateMeshJoinCode();
        return new MeshCodeDto(code, QrRows(code), runtime.VerificationCode);
    }

    public async Task<string?> ScanMeshCodeAsync(CancellationToken ct = default)
    {
        if (qrScanner is not { IsAvailable: true })
        {
            throw new ApiValidationException("This device cannot scan QR codes.");
        }

        try
        {
            var text = await qrScanner.ScanAsync(ct).ConfigureAwait(false);
            if (text is not null && !MeshFaceToFace.IsMeshCode(text))
            {
                throw new ApiValidationException("That is a computer's pairing code. Use \"Scan a code\" instead; it handles both kinds.");
            }

            return text;
        }
        catch (QrScanUnavailableException exception)
        {
            throw new ApiValidationException(exception.Message);
        }
    }

    public async Task<string?> ScanAnyCodeAsync(CancellationToken ct = default)
    {
        if (qrScanner is not { IsAvailable: true })
        {
            throw new ApiValidationException("This device cannot scan QR codes. Type the computer's code under \"Other ways\" instead.");
        }

        try
        {
            return await qrScanner.ScanAsync(ct).ConfigureAwait(false);
        }
        catch (QrScanUnavailableException exception)
        {
            throw new ApiValidationException(exception.Message);
        }
    }

    public bool IsDeviceCode(string code) => MeshFaceToFace.IsMeshCode(code);

    public MeshCodePreviewDto PreviewMeshCode(string code)
    {
        try
        {
            var preview = runtime.PreviewMeshCode(code);
            return new MeshCodePreviewDto(preview.IsJoinRequest, preview.DisplayName, preview.VerificationCode);
        }
        catch (System.FormatException exception)
        {
            throw new ApiValidationException(exception.Message);
        }
    }

    public async Task<MeshCodeDto> AdmitDeviceAsync(string joinCode, CancellationToken ct = default)
    {
        try
        {
            var welcome = await runtime.AdmitFromJoinCodeAsync(joinCode, ct).ConfigureAwait(false);
            return new MeshCodeDto(welcome, QrRows(welcome), runtime.VerificationCode);
        }
        catch (Exception exception) when (exception is System.FormatException or UserPresenceDeniedException)
        {
            throw new ApiValidationException(exception.Message);
        }
    }

    public async Task<string> JoinWithWelcomeAsync(string welcomeCode, CancellationToken ct = default)
    {
        try
        {
            var preview = runtime.PreviewMeshCode(welcomeCode);
            await runtime.JoinFromWelcomeAsync(welcomeCode, ct).ConfigureAwait(false);
            return preview.DisplayName;
        }
        catch (Exception exception) when (exception is System.FormatException or UserPresenceDeniedException)
        {
            throw new ApiValidationException(exception.Message);
        }
    }

    /// <summary>The QR modules for <paramref name="text"/>, quiet zone included, as rows of '1' (dark) and '0' (light).</summary>
    public static IReadOnlyList<string> QrRows(string text)
    {
        var matrix = Encoder.encode(text, ErrorCorrectionLevel.L, new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.CHARACTER_SET] = "ISO-8859-1",
        }).Matrix;
        const int quiet = 4;
        var size = matrix.Width + quiet * 2;
        var rows = new string[size];
        for (var y = 0; y < size; y++)
        {
            var row = new char[size];
            for (var x = 0; x < size; x++)
            {
                var mx = x - quiet;
                var my = y - quiet;
                row[x] = mx >= 0 && my >= 0 && mx < matrix.Width && my < matrix.Height && matrix[mx, my] == 1 ? '1' : '0';
            }

            rows[y] = new string(row);
        }

        return rows;
    }
}

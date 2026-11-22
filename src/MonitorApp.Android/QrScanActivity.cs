// The Android camera2 callback interfaces are bound with nullability that differs between SDK levels; implementing them
// with a fixed annotation would fail -warnaserror (CS8767) on some levels, so this file opts out of nullable analysis.
#nullable disable

using Android;
using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using MonitorApp.Client.Mobile;

namespace MonitorApp.Android;

/// <summary>
/// Full-screen camera preview that scans for a Monitor App pairing QR. Uses framework camera2: a rear-camera preview on a
/// <see cref="TextureView"/> plus a YUV_420_888 <see cref="ImageReader"/> whose luminance plane is decoded by
/// <see cref="QrFrameDecoder"/>. The activity requests the CAMERA permission itself; denial or a camera failure is reported
/// to the pending <see cref="QrScanSession"/> so the UI can fall back to the typed code. Portrait only (the preview is not
/// rotated), and it releases the camera whenever it is paused.
/// </summary>
[Activity(
    Label = "Scan pairing QR",
    Exported = false,
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public sealed class QrScanActivity : Activity, TextureView.ISurfaceTextureListener, ImageReader.IOnImageAvailableListener
{
    private const int CameraPermissionRequest = 7001;
    private const int MaximumFrameArea = 1280 * 960;

    private static QrScanActivity active;

    private TextureView textureView;
    private HandlerThread cameraThread;
    private Handler cameraHandler;
    private CameraDevice camera;
    private CameraCaptureSession session;
    private ImageReader reader;
    private bool opening;
    private int finished;

    /// <summary>Closes the scanner if it is showing (the caller cancelled).</summary>
    public static void FinishCurrent()
    {
        var activity = active;
        activity?.RunOnUiThread(() => activity.Finish());
    }

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        active = this;

        var root = new FrameLayout(this);
        root.SetBackgroundColor(Color.Black);
        root.SetFitsSystemWindows(true);

        textureView = new TextureView(this) { SurfaceTextureListener = this };
        root.AddView(textureView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var hint = new TextView(this)
        {
            Text = "Point the camera at the QR code on the computer's Monitor App admin page.",
            Gravity = GravityFlags.Center,
        };
        hint.SetTextColor(Color.White);
        hint.SetBackgroundColor(Color.Argb(160, 0, 0, 0));
        hint.SetPadding(32, 24, 32, 24);
        root.AddView(hint, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top));

        var cancel = new Button(this) { Text = "Cancel" };
        cancel.Click += (_, _) => Finish();
        var cancelLayout = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Bottom | GravityFlags.CenterHorizontal);
        cancelLayout.BottomMargin = 48;
        root.AddView(cancel, cancelLayout);
        SetContentView(root);

        if (CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.Camera], CameraPermissionRequest);
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != CameraPermissionRequest)
        {
            return;
        }

        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            OpenCamera();
        }
        else
        {
            Fail("Camera permission was denied. Allow the camera for Monitor App in system settings to scan.");
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        OpenCamera();
    }

    protected override void OnPause()
    {
        CloseCamera();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        CloseCamera();
        if (ReferenceEquals(active, this))
        {
            active = null;
        }

        // Backed out without a result: complete the pending scan with null (a no-op after a result or failure).
        QrScanSession.Close();
        base.OnDestroy();
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) => OpenCamera();

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        CloseCamera();
        return true;
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
    {
    }

    public void OnSurfaceTextureUpdated(SurfaceTexture surface)
    {
    }

    /// <summary>Runs on the camera thread for each preview frame; decodes the newest frame and drops the rest.</summary>
    public void OnImageAvailable(ImageReader reader)
    {
        Image image = null;
        try
        {
            image = reader.AcquireLatestImage();
            if (image is null || Volatile.Read(ref finished) != 0)
            {
                return;
            }

            var plane = image.GetPlanes()[0];
            var buffer = plane.Buffer;
            var luminance = new byte[buffer.Remaining()];
            buffer.Get(luminance);
            var text = QrFrameDecoder.TryDecodeInvitation(luminance, plane.RowStride, image.Width, image.Height);
            if (text is not null && Interlocked.Exchange(ref finished, 1) == 0)
            {
                QrScanSession.Complete(text);
                RunOnUiThread(Finish);
            }
        }
        catch (ArgumentException)
        {
            // A frame whose buffer does not match its stride: skip it, the next one decodes normally.
        }
        finally
        {
            image?.Close();
        }
    }

    private void OpenCamera()
    {
        if (camera is not null || opening || Volatile.Read(ref finished) != 0 ||
            !textureView.IsAvailable ||
            CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted)
        {
            return;
        }

        opening = true;
        try
        {
            var manager = (CameraManager)GetSystemService(CameraService);
            string cameraId = null;
            Size frame = null;
            foreach (var candidate in manager.GetCameraIdList())
            {
                var characteristics = manager.GetCameraCharacteristics(candidate);
                var map = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                var size = PickFrameSize(map?.GetOutputSizes((int)ImageFormatType.Yuv420888));
                if (size is null)
                {
                    continue;
                }

                var facing = (Java.Lang.Integer)characteristics.Get(CameraCharacteristics.LensFacing);
                var rear = facing is not null && facing.IntValue() == (int)LensFacing.Back;
                if (cameraId is null || rear)
                {
                    cameraId = candidate;
                    frame = size;
                }

                if (rear)
                {
                    break;
                }
            }

            if (cameraId is null)
            {
                Fail("No usable camera was found.");
                return;
            }

            ShapePreview(frame);
            reader = ImageReader.NewInstance(frame.Width, frame.Height, ImageFormatType.Yuv420888, 2);
            cameraThread = new HandlerThread("monitor-qr-scan");
            cameraThread.Start();
            cameraHandler = new Handler(cameraThread.Looper);
            reader.SetOnImageAvailableListener(this, cameraHandler);
            textureView.SurfaceTexture.SetDefaultBufferSize(frame.Width, frame.Height);
            manager.OpenCamera(cameraId, new DeviceCallback(this), cameraHandler);
        }
        catch (Java.Lang.Exception exception)
        {
            opening = false;
            Fail("The camera could not be opened (" + exception.Message + ").");
        }
    }

    private static Size PickFrameSize(Size[] sizes)
    {
        Size best = null;
        foreach (var size in sizes ?? [])
        {
            var area = size.Width * size.Height;
            if (area <= MaximumFrameArea && (best is null || area > best.Width * best.Height))
            {
                best = size;
            }
        }

        return best;
    }

    /// <summary>Portrait: the sensor delivers landscape frames, so the on-screen aspect is height:width of the buffer.</summary>
    private void ShapePreview(Size frame)
    {
        var width = textureView.RootView.Width;
        if (width <= 0)
        {
            return;
        }

        var height = (int)(width * (double)frame.Width / frame.Height);
        textureView.LayoutParameters = new FrameLayout.LayoutParams(width, height, GravityFlags.Center);
    }

    private void StartSession(CameraDevice device)
    {
        opening = false;
        if (Volatile.Read(ref finished) != 0 || reader is null)
        {
            device.Close();
            return;
        }

        camera = device;
        try
        {
            var previewSurface = new Surface(textureView.SurfaceTexture);
            var readerSurface = reader.Surface;
            var callback = new SessionCallback(this, previewSurface, readerSurface);
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                var outputs = new List<OutputConfiguration>
                {
                    new OutputConfiguration(previewSurface),
                    new OutputConfiguration(readerSurface),
                };
                device.CreateCaptureSession(new SessionConfiguration(
                    global::Android.Hardware.Camera2.Params.SessionType.Regular, outputs, MainExecutor, callback));
            }
            else
            {
                // API 26-27 only: the surface-list overload is deprecated from API 30, where the branch above is taken.
#pragma warning disable CA1422, CS0618
                device.CreateCaptureSession(new List<Surface> { previewSurface, readerSurface }, callback, cameraHandler);
#pragma warning restore CA1422, CS0618
            }
        }
        catch (Java.Lang.Exception exception)
        {
            Fail("The camera preview could not start (" + exception.Message + ").");
        }
    }

    private void StartPreview(CameraCaptureSession configured, Surface previewSurface, Surface readerSurface)
    {
        if (camera is null || Volatile.Read(ref finished) != 0)
        {
            return;
        }

        session = configured;
        try
        {
            var request = camera.CreateCaptureRequest(CameraTemplate.Preview);
            request.AddTarget(previewSurface);
            request.AddTarget(readerSurface);
            configured.SetRepeatingRequest(request.Build(), null, cameraHandler);
        }
        catch (Java.Lang.Exception exception)
        {
            Fail("The camera preview could not start (" + exception.Message + ").");
        }
    }

    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref finished, 1) == 0)
        {
            QrScanSession.Fail(message);
            RunOnUiThread(Finish);
        }
    }

    private void CloseCamera()
    {
        opening = false;
        try
        {
            session?.Close();
            camera?.Close();
            reader?.Close();
        }
        catch (Java.Lang.Exception)
        {
            // Closing an already-disconnected camera can throw; the resources are released either way.
        }

        session = null;
        camera = null;
        reader = null;
        cameraThread?.QuitSafely();
        cameraThread = null;
        cameraHandler = null;
    }

    private sealed class DeviceCallback(QrScanActivity owner) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice device) => owner.StartSession(device);

        public override void OnDisconnected(CameraDevice device)
        {
            device.Close();
            owner.Fail("The camera was disconnected.");
        }

        public override void OnError(CameraDevice device, [GeneratedEnum] CameraError error)
        {
            device.Close();
            owner.Fail("The camera reported an error (" + error + ").");
        }
    }

    private sealed class SessionCallback(QrScanActivity owner, Surface previewSurface, Surface readerSurface)
        : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession configured) =>
            owner.StartPreview(configured, previewSurface, readerSurface);

        public override void OnConfigureFailed(CameraCaptureSession failed) =>
            owner.Fail("The camera preview could not be configured.");
    }
}

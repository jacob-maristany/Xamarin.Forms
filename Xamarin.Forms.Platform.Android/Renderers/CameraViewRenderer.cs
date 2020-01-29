using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

using Android;
using Android.App;
using Android.Hardware.Camera2.Params;
using Android.Graphics;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Camera2;
using Android.Media;

#if __ANDROID_29__
using AndroidX.Core.Content;
using AndroidX.Core.App;
#else
using Android.Support.V4.Content;
using Android.Support.V4.App;
#endif

using Android.Views;
using Android.Widget;
using Android.Runtime;
using AOrientation = Android.Content.Res.Orientation;
using AVideoSource = Android.Media.VideoSource;
using AView = Android.Views.View;
using ASize = Android.Util.Size;
using App = Android.App.Application;

using Java.Lang;
using Java.Util.Concurrent;

using Xamarin.Forms.Internals;
using Xamarin.Forms.Platform.Android.FastRenderers;
using Xamarin.Forms.PlatformConfiguration.AndroidSpecific;

namespace Xamarin.Forms.Platform.Android
{
	public class CameraViewRenderer : FrameLayout, IVisualElementRenderer, IViewRenderer, TextureView.ISurfaceTextureListener
	{
		int? _defaultLabelFor;
		bool _disposed;
		CameraView _element;
		VisualElementTracker _visualElementTracker;
		VisualElementRenderer _visualElementRenderer;
		readonly MotionEventHelper _motionEventHelper;

		CameraDevice _device;
		CaptureRequest.Builder _captureBuilder;
		CameraCaptureSession _captureSession;

		AutoFitTextureView _texture;
		ImageReader _photoReader;
		MediaRecorder _mediaRecorder;
		ASize _previewSize, _videoSize, _photoSize;
		int _sensorOrientation;
		LensFacing _cameraType;

		Lazy<MediaActionSound> _mediaSound;

		bool _busy;
		bool _flashSupported;
		bool _stabilizationSupported;
		bool _previewRunning;
		ControlAEMode _flashMode;
		string _cameraId;
		string _videoFile;
		Semaphore _captureSessionOpenCloseLock;
		CameraTemplate cameraTemplate;

		float _zoom;
		bool _zoomSupported => _maxDigitalZoom != 0;
		float _maxDigitalZoom;
		Rect _activeRect;

		Lazy<CameraManager> _manager;

		bool IsRecordingVideo => _mediaRecorder != null;

		bool UseSystemSound { get; set; }

		void Sound(MediaActionSoundType soundType)
		{
			if (Element.OnThisPlatform().GetSutterSound())
				_mediaSound.Value.Play(soundType);
		}

		CameraManager Manager => _manager.Value;

		public CameraViewRenderer(Context context) : base(context)
		{
			Xamarin.Forms.CameraView.VerifyCameraViewFlagEnabled(nameof(CameraViewRenderer));
			_motionEventHelper = new MotionEventHelper();
			_mediaSound = new Lazy<MediaActionSound>(() => new MediaActionSound());
			_captureSessionOpenCloseLock = new Semaphore(1);
			_manager = new Lazy<CameraManager>(() => (CameraManager)Context.GetSystemService(Context.CameraService));
			_visualElementRenderer = new VisualElementRenderer(this);
		}

		bool IsBusy
		{
			get => _device == null || _busy;
			set
			{
				_busy = value;
				if (Element != null)
					Element.IsBusy = value;
			}
		}

		bool Available
		{
			get => Element?.IsAvailable ?? false;
			set
			{
				if (Element?.IsAvailable != value)
					Element.IsAvailable = value;
			}
		}

		ASize GetMaxSize(ASize[] ImageSizes)
		{
			ASize maxSize = null;
			long maxPixels = 0;
			for (int i = 0; i < ImageSizes.Length; i++)
			{
				long currentPixels = ImageSizes[i].Width * ImageSizes[i].Height;
				if (currentPixels > maxPixels)
				{
					maxSize = ImageSizes[i];
					maxPixels = currentPixels;
				}
			}
			return maxSize;
		}

		void RetrieveCameraDevice()
		{
			if (Context == null)
				return;

			if (IsRecordingVideo)
			{
				StopRecord();
				return;
			}

			if (_device != null)
				CloseDevice();

			if (!CheckAndRequestPermission(Manifest.Permission.Camera))
			{
				Element.RaiseMediaCaptureFailed($"No permission to use the camera.");
				return;
			}

			if (!_captureSessionOpenCloseLock.TryAcquire(2500, TimeUnit.Milliseconds))
				throw new RuntimeException("Time out waiting to lock camera opening.");

			IsBusy = true;
			_cameraId = GetCameraId();

			if (string.IsNullOrEmpty(_cameraId))
			{
				IsBusy = false;
				_captureSessionOpenCloseLock.Release();
				//_texture.ClearCanvas(Element.BackgroundColor.ToAndroid()); // HANG after select valid camera...
				Element.RaiseMediaCaptureFailed($"No {Element.CameraOptions} camera found");
			}
			else
			{
				InitializeCamera();
			}
		}

		void InitializeCamera()
		{
			if (string.IsNullOrEmpty(_cameraId))
				return;

			IsBusy = true;
			try
			{
				var characteristics = Manager.GetCameraCharacteristics(_cameraId);
				var map = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);

				_flashSupported = characteristics.Get(CameraCharacteristics.FlashInfoAvailable) == Java.Lang.Boolean.True;
				_stabilizationSupported = false;
				var stabilizationModes = characteristics.Get(CameraCharacteristics.ControlAvailableVideoStabilizationModes);
				if (stabilizationModes != null)
				{
					var modes = (int[])stabilizationModes;
					foreach (var mode in modes)
					{
						if (mode == (int)ControlVideoStabilizationMode.On)
							_stabilizationSupported = true;
					}
				}
				Element.MaxZoom = _maxDigitalZoom = (float)characteristics.Get(CameraCharacteristics.ScalerAvailableMaxDigitalZoom);
				_activeRect = (Rect)characteristics.Get(CameraCharacteristics.SensorInfoActiveArraySize);
				_previewSize = GetMaxSize(map.GetOutputSizes(Class.FromType(typeof(SurfaceTexture))));
				_photoSize = GetMaxSize(map.GetOutputSizes((int)ImageFormatType.Jpeg));
				_videoSize = GetMaxSize(map.GetOutputSizes(Class.FromType(typeof(MediaRecorder))));
				_sensorOrientation = (int)characteristics.Get(CameraCharacteristics.SensorOrientation);
				_cameraType = (LensFacing)(int)characteristics.Get(CameraCharacteristics.LensFacing);

				if (Resources.Configuration.Orientation == AOrientation.Landscape)
					_texture.SetAspectRatio(_previewSize.Width, _previewSize.Height);
				else
					_texture.SetAspectRatio(_previewSize.Height, _previewSize.Width);

				Manager.OpenCamera(
					_cameraId,
					new CameraStateListener
					{
						OnOpenedAction = OnDeviceOpened,
						OnDisconnectedAction = OnDeviceDisconnected,
						OnErrorAction = OnDeviceError,
						OnClosedAction = OnDeviceClosed
					}, 
					null);
			}
			catch (Java.Lang.Exception error)
			{
				IsBusy = false;
				LogError("Failed to open camera", error);
				Available = false;
			}
		}

		void UpdateCaptureOptions()
		{
			CameraTemplate newTemplate;
			switch (Element.CaptureOptions)
			{
				default:
				case CameraCaptureOptions.Photo:
					newTemplate = CameraTemplate.StillCapture;
					break;
				case CameraCaptureOptions.Video:
					newTemplate = CameraTemplate.Record;
					break;
			}

			if (cameraTemplate != newTemplate)
			{
				cameraTemplate = newTemplate;
				RetrieveCameraDevice();
			}
		}

		void TakePhoto()
		{
			if (IsBusy || cameraTemplate != CameraTemplate.StillCapture || _photoReader != null)
				return;

			try
			{
				_photoReader = ImageReader.NewInstance(_photoSize.Width, _photoSize.Height, ImageFormatType.Jpeg, maxImages: 1);

				var readerListener = new ImageAvailableListener();
				readerListener.Photo += (_, e) =>
				{
					if (Element.SavePhotoToFile)
						File.WriteAllBytes(ConstructMediaFilename("jpg"), e);
					Sound(MediaActionSoundType.ShutterClick);
					OnPhoto(this, e);
				};

				_photoReader.SetOnImageAvailableListener(readerListener, null);

				Capture(() => _captureSession.Capture(_captureBuilder.Build(),
					new CameraCaptureListener
					{
						OnCompleted = (_) =>
						{
							StopPreview();
							StartPreview();
						}
					},
				null));
			}
			catch (Java.Lang.Exception error)
			{
				LogError("Failed to take photo", error);
			}
		}

		string ConstructMediaFilename(string extension)
		{
			var path = Context.GetExternalMediaDirs()[0].AbsolutePath;
			path = System.IO.Path.Combine(path, "Camera");
			if (!Directory.Exists(path))
				Directory.CreateDirectory(path);
			var timeStamp = DateTime.Now.ToString("yyyyddMM_HHmmss");
			return System.IO.Path.Combine(path, $"{timeStamp}.{extension}");
		}

		void StartRecord()
		{
			if (IsBusy)
			{
				return;
			}
			else if (IsRecordingVideo)
			{
				Element?.RaiseMediaCaptureFailed("Video already recording.");
				return;
			}
			else if (cameraTemplate != CameraTemplate.Record)
			{
				Element?.RaiseMediaCaptureFailed($"Unexpected error: Camera {Element.CameraOptions} not configured to record video.");
				return;
			}

			try
			{
				bool recordAudio = CheckAndRequestPermission(Manifest.Permission.RecordAudio);

				_mediaRecorder = new MediaRecorder();
				if (recordAudio)
					_mediaRecorder.SetAudioSource(AudioSource.Camcorder);
				_mediaRecorder.SetVideoSource(AVideoSource.Surface);
				_mediaRecorder.SetOutputFormat(OutputFormat.Mpeg4);

				_mediaRecorder.SetVideoEncodingBitRate(10000000);
				_mediaRecorder.SetVideoFrameRate(30);
				_mediaRecorder.SetVideoSize(_videoSize.Width, _videoSize.Height);
				_mediaRecorder.SetVideoEncoder(VideoEncoder.H264);
				if (recordAudio)
					_mediaRecorder.SetAudioEncoder(AudioEncoder.Default);

				_videoFile = ConstructMediaFilename("mp4");
				_mediaRecorder.SetOutputFile(_videoFile);
				_mediaRecorder.SetOrientationHint(GetCaptureOrientation());
				_mediaRecorder.Prepare();

				Capture(() =>
				{
					UpdatePreview();
					Sound(MediaActionSoundType.StartVideoRecording);
					_mediaRecorder.Start();
				});
			}
			catch (Java.Lang.Exception error)
			{
				LogError("Failed to take video", error);
				Element?.RaiseMediaCaptureFailed($"Failed to take video: {error.ToString()}");
				if (_mediaRecorder != null)
				{
					_mediaRecorder.Release();
					_mediaRecorder = null;
				}
			}
		}

		void StopRecord()
		{
			if (IsBusy || !IsRecordingVideo)
				return;

			Sound(MediaActionSoundType.StopVideoRecording);
			CloseDevice();
			OnVideo(this, _videoFile);
			IsBusy = false;
			RetrieveCameraDevice();
			StartPreview();
		}

		int GetCaptureOrientation()
		{
			var jpegOrientation = _sensorOrientation + (int)GetDisplayRotation() * 90;

			if (_cameraType == LensFacing.Back)
				jpegOrientation = 180 - jpegOrientation;

			return (jpegOrientation + (360 * 2)) % 360;
		}

		void Capture(Action OnConfigured)
		{
			IsBusy = true;
			try
			{
				_captureBuilder = _device.CreateCaptureRequest(cameraTemplate);

				SetFlash();
				SetVideoStabilization();
				ApplyZoom();

				var surfaces = new List<Surface>();

				// preview texture
				if (_texture.IsAvailable && _previewSize != null)
				{
					var texture = _texture.SurfaceTexture;
					texture.SetDefaultBufferSize(_previewSize.Width, _previewSize.Height);
					surfaces.Add(new Surface(texture));
				}

				switch(cameraTemplate)
				{
					case CameraTemplate.Record:
						if (_mediaRecorder != null)
							surfaces.Add(_mediaRecorder.Surface);
						break;
					case CameraTemplate.StillCapture:
						if (_photoReader != null)
						{
							surfaces.Add(_photoReader.Surface);
							_captureBuilder.Set(CaptureRequest.ControlMode, (int)ControlMode.Auto);
							_captureBuilder.Set(CaptureRequest.JpegOrientation, GetCaptureOrientation());
						}
						break;
				}

				if (surfaces.Count == 0)
				{
					IsBusy = false;
					return;
				}

				foreach (var surface in surfaces)
					_captureBuilder.AddTarget(surface);

				_device.CreateCaptureSession(
					surfaces,
					new CameraCaptureStateListener()
					{
						OnConfigureFailedAction = (session) => {
							_captureSessionOpenCloseLock.Release();
							_previewRunning = false;
							_captureSession = session;
							IsBusy = false;
							Element.RaiseMediaCaptureFailed("Failed to start create captire sesstion");
						},
						OnConfiguredAction = (session) =>
						{
							_captureSessionOpenCloseLock.Release();
							_previewRunning = false;
							_captureSession = session;
							IsBusy = false;
							OnConfigured?.Invoke();
						}
					},
					null);
			}
			catch (Java.Lang.Exception error)
			{
				IsBusy = false;
				LogError("Capture", error);
			}
		}

		void StartPreview()
		{
			if (!_previewRunning)
				Capture(() => UpdatePreview());
		}

		void StopPreview()
		{
			_previewRunning = false;

			try
			{
				_captureSession?.StopRepeating();
			}
			catch (Java.Lang.Exception e)
			{
				LogError("Error close device", e);
			}

			// ImageReader should be closed
			if (_photoReader != null)
			{
				_photoReader.Close();
				_photoReader = null;
			}
		}

		void UpdatePreview()
		{
			if (_captureSession == null || _captureBuilder == null)
				return;

			IsBusy = true;
			try
			{
				_captureSession.SetRepeatingRequest(_captureBuilder.Build(), null, null);
				_previewRunning = true;
			}
			catch (Java.Lang.Exception error)
			{
				LogError("Update preview exception.", error);
			}
			finally
			{
				IsBusy = false;
			}
		}

		void CloseDevice()
		{
			StopPreview();
			try
			{
				if (_device != null)
				{
					_device.Close();
					_device = null;
				}

				if (_mediaRecorder != null)
				{
					_mediaRecorder.Release();
					_mediaRecorder = null;
				}
			}
			catch (Java.Lang.Exception e)
			{
				LogError("Error close device", e);
			}
		}

		void UpdateBackgroundColor()
		{
			if (_device != null && _texture.IsAvailable && _previewSize != null)
				SetBackgroundColor(Element.BackgroundColor.ToAndroid());
		}

		void SetFlash()
		{
			if (_captureBuilder == null || !_flashSupported)
				return;

			var previewFlash = FlashMode.Off;

			switch (Element.FlashMode)
			{
				default:
				case CameraFlashMode.Off:
					_flashMode = ControlAEMode.Off;
					break;
				case CameraFlashMode.On:
					_flashMode = ControlAEMode.On;
					_captureBuilder.Set(CaptureRequest.ControlMode, (int)ControlMode.Auto);
					break;
				case CameraFlashMode.Auto:
					_flashMode = ControlAEMode.OnAutoFlash;
					_captureBuilder.Set(CaptureRequest.ControlMode, (int)ControlMode.Auto);
					break;
				case CameraFlashMode.Torch:
					_flashMode = ControlAEMode.On;
					previewFlash = FlashMode.Torch;
					break;
			}

			_captureBuilder.Set(CaptureRequest.ControlAeMode, (int)_flashMode);
			_captureBuilder.Set(CaptureRequest.FlashMode, (int)previewFlash);
		}

		bool SetVideoStabilization()
		{
			if (_captureBuilder == null || !_stabilizationSupported)
			{
				_captureBuilder.Set(CaptureRequest.ControlVideoStabilizationMode,
					(int)(Element.VideoStabilization ? ControlVideoStabilizationMode.On : ControlVideoStabilizationMode.Off));
				return true;
			}
			return false;
		}

		void ApplyZoom()
		{
			if (_zoomSupported)
				_captureBuilder?.Set(CaptureRequest.ScalerCropRegion, GetZoomRect());
		}

		Rect GetZoomRect()
		{
			if (_activeRect == null)
				return null;
			var width = _activeRect.Width();
			var heigth = _activeRect.Height();
			var newWidth = (int)(width / _zoom);
			var newHeight = (int)(heigth / _zoom);
			var x = (width - newWidth) / 2;
			var y = (heigth - newHeight) / 2;
			return new Rect(x, y, x + newWidth, y + newHeight);
		}

		void LogError(string desc, Java.Lang.Exception ex)
		{
			var newLine = Environment.NewLine;
			var sb = new StringBuilder(desc);
			if (ex != null)
			{
				sb.Append($"{newLine}ErrorMessage: {ex.Message}{newLine}Stacktrace: {ex.StackTrace}");
				ex.PrintStackTrace();
			}
			Log.Warning("CameraView", sb.ToString());
		}

		string GetCameraId()
		{
			var cameraIdList = Manager.GetCameraIdList();
			if (cameraIdList.Length == 0)
				return null;

			string FilterCameraByLens(LensFacing lensFacing)
			{
				foreach (var id in cameraIdList)
				{
					var characteristics = Manager.GetCameraCharacteristics(id);
					if (lensFacing == (LensFacing)(int)characteristics.Get(CameraCharacteristics.LensFacing))
						return id;
				}
				return null;
			}

			switch (Element.CameraOptions)
			{
				default:
				case CameraOptions.Default:
					return cameraIdList.Length != 0 ? cameraIdList[0] : null;
				case CameraOptions.Front:
					return FilterCameraByLens(LensFacing.Front);
				case CameraOptions.Back:
					return FilterCameraByLens(LensFacing.Back);
				case CameraOptions.External:
					return FilterCameraByLens(LensFacing.External);
			}
		}

		bool CheckAndRequestPermission(string permission)
		{
			if (ContextCompat.CheckSelfPermission(Context, permission) == Permission.Granted)
				return true;

			if (Context is Activity activity)
				ActivityCompat.RequestPermissions(activity, new[] { permission }, 1);
			return false;
		}

		SurfaceOrientation GetDisplayRotation()
			=> App.Context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>().DefaultDisplay.Rotation;

		int GetPreviewOrientation()
		{
			switch (GetDisplayRotation())
			{
				case SurfaceOrientation.Rotation90: return 270;
				case SurfaceOrientation.Rotation180: return 180;
				case SurfaceOrientation.Rotation270: return 90;
				default: return 0;
			}
		}

		void ConfigureTransform(int viewWidth, int viewHeight)
		{
			if (_texture == null || _previewSize == null  || _previewSize.Width == 0 || _previewSize.Height == 0)
				return;

			var matrix = new Matrix();
			var viewRect = new RectF(0, 0, viewWidth, viewHeight);
			var bufferRect = new RectF(0, 0, _previewSize.Height, _previewSize.Width);
			var centerX = viewRect.CenterX();
			var centerY = viewRect.CenterY();
			bufferRect.Offset(centerX - bufferRect.CenterX(), centerY - bufferRect.CenterY());

			var mirror = Element.CameraOptions == CameraOptions.Front && Element.OnThisPlatform().GetMirrorFrontPreview();
			matrix.SetRectToRect(viewRect, bufferRect, Matrix.ScaleToFit.Fill);
			float scaleHH() => (float)viewHeight / _previewSize.Height;
			float scaleHW() => (float)viewHeight / _previewSize.Width;
			float scaleWW() => (float)viewWidth / _previewSize.Width;
			float scaleWH() => (float)viewWidth / _previewSize.Height;
			float sx = 0, sy = 0;

			switch (Element.PreviewAspect)
			{
				default:
				case Aspect.AspectFit:
					sx = sy = System.Math.Min(scaleHH(), scaleHW());
					break;
				case Aspect.AspectFill:
					sx = sy = System.Math.Max(scaleHH(), scaleHW());
					break;
				case Aspect.Fill:
					if (Resources.Configuration.Orientation == AOrientation.Landscape)
					{
						sx = scaleWW();
						sy = scaleHH();
					}
					else
					{
						sx = scaleWH();
						sy = scaleHW();
					}
					break;
			}

			matrix.PostScale(mirror ? -sx : sx, sy, centerX, centerY);
			matrix.PostRotate(GetPreviewOrientation(), centerX, centerY);
			_texture.SetTransform(matrix);
		}

		public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
		{
			ConfigureTransform(width, height);
			if (_device == null && !_busy)
				RetrieveCameraDevice();
		}

		public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
		{
			ConfigureTransform(width, height);
		}

		public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
		{
			CloseDevice();
			return true;
		}

		public void OnSurfaceTextureUpdated(SurfaceTexture surface)
		{
		}

		public event EventHandler<VisualElementChangedEventArgs> ElementChanged;

		public event EventHandler<PropertyChangedEventArgs> ElementPropertyChanged;

		void OnElementPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			ElementPropertyChanged?.Invoke(this, e);

			switch (e.PropertyName)
			{
				case nameof(CameraView.CameraOptions):
					RetrieveCameraDevice();
					break;
				case nameof(CameraView.CaptureOptions):
					UpdateCaptureOptions();
					break;
				case nameof(CameraView.FlashMode):
					SetFlash();
					UpdatePreview();
					break;
				case nameof(CameraView.Zoom):
					_zoom = System.Math.Max(1f, System.Math.Min(Element.Zoom, _maxDigitalZoom));
					ApplyZoom();
					UpdatePreview();
					break;
				case nameof(CameraView.VideoStabilization):
					if (SetVideoStabilization())
						UpdatePreview();
					break;
				case nameof(CameraView.PreviewAspect):
				case "MirrorFrontPreview":
					if (_texture != null)
						ConfigureTransform(_texture.Width, _texture.Height);
					break;
				case nameof(CameraView.KeepScreenOn):
					if (_texture != null)
						_texture.KeepScreenOn = Element.KeepScreenOn;
					break;
			}
		}

		void UpdateLayoutParameters()
		{
			if (_texture.Width == 0 || _texture.Height == 0)
				_texture.LayoutParameters = new LayoutParams(Width, Height, GravityFlags.NoGravity);
		}

		void OnDeviceOpened(CameraDevice device)
		{
			_device = device;
			IsBusy = false;
			StartPreview();
			Available = true;
		}

		void OnDeviceDisconnected(CameraDevice device)
		{
			CloseDevice();
			IsBusy = false;
			Available = false;
		}

		void OnDeviceError(CameraDevice device, CameraError error)
		{
			CloseDevice();
			IsBusy = false;
			Available = false;
			Element.RaiseMediaCaptureFailed($"Camera device error: {error}");
		}

		void OnDeviceClosed(CameraDevice device)
		{
			if (_device != device)
				return;

			CloseDevice();
			IsBusy = false;
			Available = false;
		}

		void OnElementChanged(ElementChangedEventArgs<CameraView> e)
		{
			if (e.OldElement != null)
			{
				e.OldElement.PropertyChanged -= OnElementPropertyChanged;
				e.OldElement.ShutterClicked -= OnShutterClicked;

				if (_texture != null)
				{
					_texture.Dispose();
					_texture = null;
				}
			}

			if (e.NewElement != null)
			{
				this.EnsureId();

				e.NewElement.PropertyChanged += OnElementPropertyChanged;
				e.NewElement.ShutterClicked += OnShutterClicked;

				if (_texture == null)
				{
					_texture = new AutoFitTextureView(Context)
					{
						SurfaceTextureListener = this,
						KeepScreenOn = e.NewElement.KeepScreenOn,
					};

					AddView(_texture, -1, -1);
				}

				UpdateLayoutParameters();
				UpdateBackgroundColor();

				ElevationHelper.SetElevation(this, e.NewElement);

				UpdateCaptureOptions();
			}
			ElementChanged?.Invoke(this, new VisualElementChangedEventArgs(e.OldElement, e.NewElement));
		}

		VisualElement IVisualElementRenderer.Element => Element;

		ViewGroup IVisualElementRenderer.ViewGroup => null;

		VisualElementTracker IVisualElementRenderer.Tracker => _visualElementTracker;

		AView IVisualElementRenderer.View => this;

		SizeRequest IVisualElementRenderer.GetDesiredSize(int widthConstraint, int heightConstraint)
		{
			Measure(widthConstraint, heightConstraint);
			var result = new SizeRequest(new Size(MeasuredWidth, MeasuredHeight), new Size(Context.ToPixels(20), Context.ToPixels(20)));
			return result;
		}

		protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
		{
			base.OnLayout(changed, left, top, right, bottom);

			if (_texture == null)
				return;

			UpdateLayoutParameters();
			var msw = MeasureSpec.MakeMeasureSpec(right - left, MeasureSpecMode.Exactly);
			var msh = MeasureSpec.MakeMeasureSpec(bottom - top, MeasureSpecMode.Exactly);

			_texture.Measure(msw, msh);
			_texture.Layout(0, 0, right - left, bottom - top);
		}

		void IVisualElementRenderer.SetElement(VisualElement element)
		{
			if (!(element is CameraView camera))
				throw new ArgumentException($"{nameof(element)} must be of type {nameof(CameraView)}");

			Performance.Start(out string reference);

			_motionEventHelper.UpdateElement(element);

			if (_visualElementTracker == null)
				_visualElementTracker = new VisualElementTracker(this);

			Element = camera;

			Performance.Stop(reference);
		}

		void IVisualElementRenderer.SetLabelFor(int? id)
		{
			if (_defaultLabelFor == null)
				_defaultLabelFor = LabelFor;

			LabelFor = (int)(id ?? _defaultLabelFor);
		}

		void IVisualElementRenderer.UpdateLayout()
		{
			var lp = _texture.LayoutParameters;
			lp.Width = Width;
			lp.Height = Height;
			_texture.LayoutParameters = lp;

			_visualElementTracker?.UpdateLayout();
		}

		void IViewRenderer.MeasureExactly() => ViewRenderer.MeasureExactly(this, Element, Context);

		CameraView Element
		{
			get => _element;
			set
			{
				if (_element == value)
					return;

				var oldElement = _element;
				_element = value;

				OnElementChanged(new ElementChangedEventArgs<CameraView>(oldElement, _element));
				_element?.SendViewInitialized(this);
			}
		}

		public override bool OnTouchEvent(MotionEvent e)
		{
			if (_visualElementRenderer.OnTouchEvent(e) || base.OnTouchEvent(e))
				return true;

			return _motionEventHelper.HandleMotionEvent(Parent, e);
		}

		protected override void Dispose(bool disposing)
		{
			if (_disposed)
				return;

			CloseDevice();

			_disposed = true;

			if (disposing)
			{
				SetOnClickListener(null);
				SetOnTouchListener(null);
				if (_visualElementTracker != null)
				{
					_visualElementTracker.Dispose();
					_visualElementTracker = null;
				}

				if (_visualElementRenderer != null)
				{
					_visualElementRenderer.Dispose();
					_visualElementRenderer = null;
				}

				if (Element != null)
				{
					Element.PropertyChanged -= OnElementPropertyChanged;
					Element.ShutterClicked -= OnShutterClicked;

					if (Platform.GetRenderer(Element) == this)
						Element.ClearValue(Platform.RendererProperty);
				}
			}

			base.Dispose(disposing);
		}

		void OnShutterClicked(object sender, EventArgs e)
		{
			switch (Element.CaptureOptions)
			{
				default:
				case CameraCaptureOptions.Default:
				case CameraCaptureOptions.Photo:
					TakePhoto();
					break;
				case CameraCaptureOptions.Video:
					if (!IsRecordingVideo)
						StartRecord();
					else
						StopRecord();
					break;
			}
		}

		void OnPhoto(object sender, byte[] data)
		{
			Device.BeginInvokeOnMainThread(() =>
			{
				Element?.RaiseMediaCaptured(new MediaCapturedEventArgs()
				{
					Data = data,
					Image = ImageSource.FromStream(() => new MemoryStream(data))
				});
			});
		}

		void OnVideo(object sender, string data)
		{
			Device.BeginInvokeOnMainThread(() =>
			{
				Element?.RaiseMediaCaptured(new MediaCapturedEventArgs()
				{
					Data = data,
					Video = MediaSource.FromUri(new Uri(data))
				});
			});
		}
	}
}
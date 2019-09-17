using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Xamarin.Forms
{
	[TypeConverter(typeof(VideoSourceConverter))]
	public abstract class VideoSource : Element
	{
		readonly object _synchandle = new object();
		CancellationTokenSource _cancellationTokenSource;

		TaskCompletionSource<bool> _completionSource;

		readonly WeakEventManager _weakEventManager = new WeakEventManager();

		protected VideoSource()
		{
		}

		protected CancellationTokenSource CancellationTokenSource
		{
			get { return _cancellationTokenSource; }
			private set
			{
				if (_cancellationTokenSource == value)
					return;
				if (_cancellationTokenSource != null)
					_cancellationTokenSource.Cancel();
				_cancellationTokenSource = value;
			}
		}

		bool IsLoading
		{
			get { return _cancellationTokenSource != null; }
		}

		public virtual Task<bool> Cancel()
		{
			if (!IsLoading)
				return Task.FromResult(false);

			var tcs = new TaskCompletionSource<bool>();
			TaskCompletionSource<bool> original = Interlocked.CompareExchange(ref _completionSource, tcs, null);
			if (original == null)
			{
				_cancellationTokenSource.Cancel();
			}
			else
				tcs = original;

			return tcs.Task;
		}

		public static VideoSource FromResource(string resource, Type resolvingType)
		{
			return FromResource(resource, resolvingType.GetTypeInfo().Assembly);
		}

		public static VideoSource FromResource(string resource, Assembly sourceAssembly = null)
		{
#if NETSTANDARD2_0
			sourceAssembly = sourceAssembly ?? Assembly.GetCallingAssembly();
#else
			if (sourceAssembly == null)
			{
				MethodInfo callingAssemblyMethod = typeof(Assembly).GetTypeInfo().GetDeclaredMethod("GetCallingAssembly");
				if (callingAssemblyMethod != null)
				{
					sourceAssembly = (Assembly)callingAssemblyMethod.Invoke(null, new object[0]);
				}
				else
				{
					Internals.Log.Warning("Warning", "Can not find CallingAssembly, pass resolvingType to FromResource to ensure proper resolution");
					return null;
				}
			}
#endif
			return FromStream(() => sourceAssembly.GetManifestResourceStream(resource));
		}

		public static VideoSource FromFile(string file)
		{
			return new FileVideoSource { File = file };
		}

		public static VideoSource FromStream(Func<Stream> stream)
		{
			return new StreamVideoSource { Stream = token => Task.Run(stream, token) };
		}

		public static VideoSource FromUri(Uri uri)
		{
			if (!uri.IsAbsoluteUri)
				throw new ArgumentException("uri is relative");
			return new UriVideoSource { Uri = uri };
		}

		public static implicit operator VideoSource(string source)
		{
			return Uri.TryCreate(source, UriKind.Absolute, out Uri uri) && uri.Scheme != "file" ? FromUri(uri) : FromFile(source);
		}

		public static implicit operator VideoSource(Uri uri)
		{
			if (uri == null)
				return null;

			if (!uri.IsAbsoluteUri)
				throw new ArgumentException("uri is relative");
			return FromUri(uri);
		}

		protected void OnLoadingCompleted(bool cancelled)
		{
			if (!IsLoading || _completionSource == null)
				return;

			TaskCompletionSource<bool> tcs = Interlocked.Exchange(ref _completionSource, null);
			if (tcs != null)
				tcs.SetResult(cancelled);

			lock (_synchandle)
			{
				CancellationTokenSource = null;
			}
		}

		protected void OnLoadingStarted()
		{
			lock (_synchandle)
			{
				CancellationTokenSource = new CancellationTokenSource();
			}
		}

		protected void OnSourceChanged()
		{
			_weakEventManager.HandleEvent(this, EventArgs.Empty, nameof(SourceChanged));
		}

		internal event EventHandler SourceChanged
		{
			add { _weakEventManager.AddEventHandler(value); }
			remove { _weakEventManager.RemoveEventHandler(value); }
		}
	}
}
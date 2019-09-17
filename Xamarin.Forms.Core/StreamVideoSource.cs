using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Xamarin.Forms
{
	public class StreamVideoSource : VideoSource, IStreamImageSource
	{
		public static readonly BindableProperty StreamProperty = BindableProperty.Create("Stream", typeof(Func<CancellationToken, Task<Stream>>), typeof(StreamVideoSource),
			default(Func<CancellationToken, Task<Stream>>));

		public virtual Func<CancellationToken, Task<Stream>> Stream
		{
			get => (Func<CancellationToken, Task<Stream>>)GetValue(StreamProperty);
			set => SetValue(StreamProperty, value);
		}

		protected override void OnPropertyChanged(string propertyName)
		{
			if (propertyName == StreamProperty.PropertyName)
				OnSourceChanged();
			base.OnPropertyChanged(propertyName);
		} 

		async Task<Stream> IStreamImageSource.GetStreamAsync(CancellationToken userToken)
		{
			if (Stream == null)
				return null;

			OnLoadingStarted();
			userToken.Register(CancellationTokenSource.Cancel);
			try
			{
				Stream stream = await Stream(CancellationTokenSource.Token);
				OnLoadingCompleted(false);
				return stream;
			}
			catch (OperationCanceledException)
			{
				OnLoadingCompleted(true);
				throw;
			}
		}
	}
}
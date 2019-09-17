using System;

namespace Xamarin.Forms
{
	[Xaml.TypeConversion(typeof(VideoSource))]
	public sealed class VideoSourceConverter : TypeConverter
	{
		public override object ConvertFromInvariantString(string value)
		{
			if (value != null)
			{
				return Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && uri.Scheme != "file" ? VideoSource.FromUri(uri) : VideoSource.FromFile(value);
			}

			throw new InvalidOperationException($"Cannot convert \"{value}\" into {typeof(VideoSource)}");
		}
	}
}
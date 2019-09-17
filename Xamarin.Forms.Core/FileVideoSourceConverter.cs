using System;

namespace Xamarin.Forms
{
	[Xaml.TypeConversion(typeof(FileVideoSource))]
	public sealed class FileVideoSourceConverter : TypeConverter
	{
		public override object ConvertFromInvariantString(string value)
		{
			if (value != null)
				return (FileVideoSource)VideoSource.FromFile(value);

			throw new InvalidOperationException($"Cannot convert \"{value}\" into {typeof(FileVideoSource)}");
		}
	}
}
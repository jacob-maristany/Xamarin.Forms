using System.Diagnostics;
using System.Threading.Tasks;

namespace Xamarin.Forms
{
	[TypeConverter(typeof(FileVideoSourceConverter))]
	[DebuggerDisplay("File {File}")]
	public sealed class FileVideoSource : VideoSource
	{
		public static readonly BindableProperty FileProperty = BindableProperty.Create(nameof(File), typeof(string), typeof(FileVideoSource), default(string));

		public string File
		{
			get => (string)GetValue(FileProperty);
			set => SetValue(FileProperty, value);
		}

		public override Task<bool> Cancel() => Task.FromResult(false);

		public static implicit operator FileVideoSource(string file) => (FileVideoSource)FromFile(file);

		public static implicit operator string(FileVideoSource file) => file?.File;

		protected override void OnPropertyChanged(string propertyName = null)
		{
			if (propertyName == FileProperty.PropertyName)
				OnSourceChanged();
			base.OnPropertyChanged(propertyName);
		}
	}
}
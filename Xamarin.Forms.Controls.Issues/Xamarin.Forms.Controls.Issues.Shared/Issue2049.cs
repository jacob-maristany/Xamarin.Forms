﻿using Xamarin.Forms.CustomAttributes;
using Xamarin.Forms.Internals;
using System;
using System.Threading;
using System.Globalization;

#if UITEST
using Xamarin.UITest;
using NUnit.Framework;
#endif

namespace Xamarin.Forms.Controls.Issues
{
	[Preserve(AllMembers = true)]
	[Issue(IssueTracker.Github, 2049, "Bound DateTimes Display in en-US Format Not Local Format", PlatformAffected.All)]
	public class Issue2049 : TestContentPage
	{
		[Preserve(AllMembers = true)]
		public class Model
		{
			public DateTime TheDate { get; set; }
			public float FloatValue { get; set; }
		}

		DateTime testDate = DateTime.ParseExact("2077-12-31T13:55:56", "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
		int _localeIndex = 0;
		string[] _localeIds = new[] { "en-US", "ru-RU", "en-AU", "zh-Hans" };
		string _instuctions = $"When you change the locale, the date format must change.{Environment.NewLine}Current locale: ";
		Label descLabel = new Label(); 

		protected override void Init()
		{
			UpdateContext();

			var label = new Label();
			label.SetBinding(Label.TextProperty, nameof(Model.TheDate));

			var labelFloat = new Label();
			labelFloat.SetBinding(Label.TextProperty, nameof(Model.FloatValue));

			UpdateDescLabel();

			var layout = new StackLayout
			{
				Children = {
					descLabel,
					new Button()
					{
						Text = "Change Locale",
						Command = new Command(() => ChangeLocale())
					},
					label,
					labelFloat
				}
			};

			Content = layout;
		}

		void UpdateContext()
		{
			BindingContext = new Model
			{
				TheDate = DateTime.ParseExact("2077-12-31T13:55:56", "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
				FloatValue = 123.456f // in some locales, the decimal symbol may not be a dot.
			};
		}

		void ChangeLocale()
		{
			if (++_localeIndex > _localeIds.Length - 1)
				_localeIndex = 0;
			var locale = _localeIds[_localeIndex];

			Thread.CurrentThread.CurrentCulture = new CultureInfo(locale);
			UpdateDescLabel();
			UpdateContext();
		}

		void UpdateDescLabel() => descLabel.Text = $"{_instuctions}{Thread.CurrentThread.CurrentCulture.DisplayName}";

#if UITEST
		[Test]
		public void Issue2049Test ()
		{
			foreach (var locale in _localeIds)
			{
				if (RunningApp.Query(query => query.Text(testDate.ToString(new CultureInfo(locale)))).Length != 1)
					Assert.Fail();
				RunningApp.Tap("Change Locale");
			}
		} 
#endif
	}
}
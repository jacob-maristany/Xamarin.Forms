﻿using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Xamarin.Forms.PlatformConfiguration.WindowsSpecific;
using Specifics = Xamarin.Forms.PlatformConfiguration.WindowsSpecific.Page;

namespace Xamarin.Forms.Platform.UWP
{
	public class PageRenderer : VisualElementRenderer<Page, FrameworkElement>
	{
		bool _disposed;

		bool _loaded;

		protected override AutomationPeer OnCreateAutomationPeer()
		{
			// Pages need an automation peer so we can interact with them in automated tests
			return new FrameworkElementAutomationPeer(this);
		}

		protected override void Dispose(bool disposing)
		{
			if (!disposing || _disposed)
				return;

			_disposed = true;

			if (Element != null)
			{
				ReadOnlyCollection<Element> children = ((IElementController)Element).LogicalChildren;
				for (var i = 0; i < children.Count; i++)
				{
					var visualChild = children[i] as VisualElement;
					visualChild?.Cleanup();
				}
				Element?.SendDisappearing();
			}

			base.Dispose();
		}

		protected override void OnElementChanged(ElementChangedEventArgs<Page> e)
		{
			base.OnElementChanged(e);

			e.OldElement?.SendDisappearing();

			if (e.NewElement != null)
			{
				if (e.OldElement == null)
				{
					Loaded += OnLoaded;
					Tracker = new BackgroundTracker<FrameworkElement>(BackgroundProperty);
				}

				if (!string.IsNullOrEmpty(Element.AutomationId))
				{
					SetAutomationId(Element.AutomationId);
				}

				if (_loaded)
					e.NewElement.SendAppearing();

				UpdateImageDirectory();
			}
		}

		protected override void OnElementPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			base.OnElementPropertyChanged(sender, e);
			if (e.PropertyName == Specifics.ImageDirectoryProperty.PropertyName)
				UpdateImageDirectory();
		}

		void UpdateImageDirectory()
		{
			string path = Element.IsSet(Specifics.ImageDirectoryProperty)
				? Element.OnThisPlatform().GetImageDirectory()
				: null;

			foreach (var element in Element.Descendants())
			{
				switch (element)
				{
					case Page p:
						p.OnThisPlatform().SetImageDirectory(path);
						break;
					case Image i:
						i.OnThisPlatform().SetImageDirectory(path);
						break;
				}
			}
		}

		void OnLoaded(object sender, RoutedEventArgs args)
		{
			var carouselPage = Element?.Parent as CarouselPage;
			if (carouselPage != null && carouselPage.Children[0] != Element)
			{
				return;
			}
			_loaded = true;
			Unloaded += OnUnloaded;
			Element?.SendAppearing();
		}

		void OnUnloaded(object sender, RoutedEventArgs args)
		{
			Unloaded -= OnUnloaded;
			_loaded = false;
			Element?.SendDisappearing();
		}
	}
}
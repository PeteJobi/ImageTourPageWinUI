using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ImageTourPage.Controls
{
    public sealed partial class TimespanTextBox : UserControl
    {
        public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
            nameof(Minimum),
            typeof(TimeSpan),
            typeof(TimespanTextBox),
            new PropertyMetadata(TimeSpan.Zero, OnMinimumChanged));
        public TimeSpan Minimum
        {
            get => (TimeSpan)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
            nameof(Maximum),
            typeof(TimeSpan),
            typeof(TimespanTextBox),
            new PropertyMetadata(TimeSpan.MaxValue, OnMaximumChanged));
        public TimeSpan Maximum
        {
            get => (TimeSpan)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value),
            typeof(TimeSpan),
            typeof(TimespanTextBox),
            new PropertyMetadata(TimeSpan.Zero, OnValueChanged));
        public TimeSpan Value
        {
            get => (TimeSpan)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public static readonly DependencyProperty NoMaskProperty = DependencyProperty.Register(
            nameof(NoMask),
            typeof(bool),
            typeof(TimespanTextBox),
            new PropertyMetadata(false, OnMaskChanged));

        public bool NoMask
        {
            get => (bool)GetValue(NoMaskProperty);
            set => SetValue(NoMaskProperty, value);
        }

        public TimespanTextBox()
        {
            InitializeComponent();
        }

        private void Text_OnLoaded(object sender, RoutedEventArgs e)
        {
            var textBox = (TextBox)sender;
            var delButton = FindChildElementByName(textBox, "DeleteButton") as Control;
            if (delButton == null) return;
            var parentGrid = delButton.Parent;
            if (parentGrid != null)
            {
                ((Grid)parentGrid).Children.Remove(delButton);
            }
        }

        private static void OnMinimumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var textBox = (TimespanTextBox)d;
            if (textBox.Value < textBox.Minimum) textBox.Value = textBox.Minimum;
            //e has e.NewValue and e.OldValue
        }

        private static void OnMaximumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var textBox = (TimespanTextBox)d;
            if (textBox.Value > textBox.Maximum) textBox.Value = textBox.Maximum;
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var textBox = (TimespanTextBox)d;
            //Debug.WriteLine($"VC {textBox.Name} {textBox.Value}");
            if (textBox.Value < textBox.Minimum) textBox.Value = textBox.Minimum;
            else if (textBox.Value > textBox.Maximum) textBox.Value = textBox.Maximum;
        }

        private static void OnMaskChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var textBox = (TextBox)VisualTreeHelper.GetChild(d, 0);
            TextBoxExtensions.SetMask(textBox, (bool)e.NewValue ? null : "99:99:99.999");
        }

        public static DependencyObject? FindChildElementByName(DependencyObject tree, string sName)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(tree); i++)
            {
                var child = VisualTreeHelper.GetChild(tree, i);
                if (child != null && ((FrameworkElement)child).Name == sName)
                    return child;
                var childInSubtree = FindChildElementByName(child, sName);
                if (childInSubtree != null)
                    return childInSubtree;
            }
            return null;
        }
    }

    internal class TimespanToTextConverter : IValueConverter
    {

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            //Debug.WriteLine($"CO {value}");
            if (value is TimeSpan timeSpan)
            {
                return $@"{timeSpan:hh\:mm\:ss\.fff}";
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            //Debug.WriteLine($"CB {value}");
            if (value is string str && TimeSpan.TryParse(str, out var timeSpan))
            {
                Debug.WriteLine($"CBB {timeSpan}");
                return timeSpan;
            }

            return TimeSpan.Zero;
        }
    }
}

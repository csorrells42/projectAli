using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Viewports.Diagnostics;

public partial class PipelineTimingWindow : Window
{
	public sealed record TimingDisplayRow(
		string Module,
		string TimeWaitedMilliseconds,
		string TimeWorkedMilliseconds);

	private readonly IFramePipelineTimingReportSource _source;

	private readonly DispatcherTimer _requestTimer;

	private bool _stopped;

	public ObservableCollection<TimingDisplayRow> Rows { get; } = new();

	public PipelineTimingWindow(
		IFramePipelineTimingReportSource source)
	{
		_source = source
			?? throw new ArgumentNullException(nameof(source));
		InitializeComponent();
		DataContext = this;
		_requestTimer = new DispatcherTimer(
			DispatcherPriority.Background,
			Dispatcher)
		{
			Interval = TimeSpan.FromSeconds(1)
		};
		_requestTimer.Tick += RequestTimingReport;
		Loaded += WindowLoaded;
		Closed += WindowClosed;
	}

	private void WindowLoaded(object sender, RoutedEventArgs e)
	{
		RequestTimingReport(this, EventArgs.Empty);
		_requestTimer.Start();
	}

	private void WindowClosed(object? sender, EventArgs e)
	{
		StopRequesting();
		Loaded -= WindowLoaded;
		Closed -= WindowClosed;
	}

	private void RequestTimingReport(object? sender, EventArgs e)
	{
		if (_stopped)
		{
			return;
		}

		try
		{
			IReadOnlyList<FramePipelineTimingRow> report =
				_source.GetTimingReport();
			Rows.Clear();
			foreach (FramePipelineTimingRow row in report)
			{
				Rows.Add(new TimingDisplayRow(
					row.Module,
					FormatMilliseconds(row.TimeWaited),
					FormatMilliseconds(row.TimeWorked)));
			}
		}
		catch (ObjectDisposedException)
		{
			StopRequesting();
		}
	}

	private void StopRequesting()
	{
		if (_stopped)
		{
			return;
		}
		_stopped = true;
		_requestTimer.Stop();
		_requestTimer.Tick -= RequestTimingReport;
	}

	private static string FormatMilliseconds(TimeSpan value)
	{
		return value.TotalMilliseconds.ToString(
			"0.000",
			CultureInfo.InvariantCulture);
	}
}

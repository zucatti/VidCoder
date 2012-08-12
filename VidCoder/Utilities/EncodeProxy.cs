﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VidCoder
{
	using System.Diagnostics;
	using System.Globalization;
	using System.IO;
	using System.ServiceModel;
	using System.Threading.Tasks;
	using System.Timers;
	using HandBrake.Interop;
	using HandBrake.Interop.Model;
	using Model;
	using Properties;
	using Services;
	using VidCoderWorker;

	using Microsoft.Practices.Unity;

	public class EncodeProxy : IHandBrakeEncoderCallback
	{
		private const double PingTimerIntervalMs = 2000;

		public event EventHandler EncodeStarted;

		/// <summary>
		/// Fires for progress updates when encoding.
		/// </summary>
		public event EventHandler<EncodeProgressEventArgs> EncodeProgress;

		/// <summary>
		/// Fires when an encode has completed.
		/// </summary>
		public event EventHandler<EncodeCompletedEventArgs> EncodeCompleted;

		private DuplexChannelFactory<IHandBrakeEncoder> pipeFactory;
		private IHandBrakeEncoder channel;
		private ILogger logger;

		// Timer that pings the worker process periodically to see if it's still alive.
		private Timer pingTimer;

		private bool encoding;

		// Lock to take before interacting with the encoder process or changing encoding state.
		private object encoderLock = new object();

		public bool IsEncodeStarted { get; private set; }

		public void StartEncode(EncodeJob job, bool preview, int previewNumber, int previewSeconds, double overallSelectedLengthSeconds)
		{
			this.logger = Unity.Container.Resolve<ILogger>();

			var task = new Task(() =>
			    {
					var startInfo = new ProcessStartInfo(
						"VidCoderWorker.exe",
						Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
					startInfo.RedirectStandardOutput = true;
					startInfo.UseShellExecute = false;
					startInfo.CreateNoWindow = true;
					Process worker = Process.Start(startInfo);

					// When the process writes out a line, it's pipe server is ready and can be contacted for
					// work. Reading line blocks until this happens.
					Debug.WriteLine(worker.StandardOutput.ReadLine());

					lock (this.encoderLock)
					{
						try
						{
							this.pipeFactory = new DuplexChannelFactory<IHandBrakeEncoder>(
								this,
								new NetNamedPipeBinding(),
								new EndpointAddress("net.pipe://localhost/VidCoderWorker_" + worker.Id));

							this.channel = this.pipeFactory.CreateChannel();

							this.channel.StartEncode(job, preview, previewNumber, previewSeconds, overallSelectedLengthSeconds,
							                         Settings.Default.LogVerbosity);
						}
						catch (CommunicationException)
						{
							this.logger.LogError("Unable to contact encode proxy.");
							this.EndEncode(error: true);
							return;
						}
					}

			    	this.pingTimer = new Timer
					{
						AutoReset = true,
						Interval = PingTimerIntervalMs
					};

					this.pingTimer.Elapsed += (o, e) =>
					{
						lock (this.encoderLock)
						{
							if (!this.encoding)
							{
								return;
							}
						}

						if (this.encoding)
						{
							try
							{
								this.channel.Ping();
							}
							catch (CommunicationException)
							{
								lock (this.encoderLock)
								{
									this.logger.LogError("Worker process has crashed.");
									this.EndEncode(error: true);
								}
							}
						}
					};

					this.pingTimer.Start();
			    });

			this.encoding = true;
			task.Start();
		}

		public void PauseEncode()
		{
			lock (this.encoderLock)
			{
				if (this.channel != null)
				{
					try
					{
						this.channel.PauseEncode();
					}
					catch (CommunicationException)
					{
						this.logger.LogError("Unable to contact encode proxy.");
						this.EndEncode(error: true);
					}
				}
			}
		}

		public void ResumeEncode()
		{
			lock (this.encoderLock)
			{
				if (this.channel != null)
				{
					try
					{
						this.channel.ResumeEncode();
					}
					catch (CommunicationException)
					{
						this.logger.LogError("Unable to contact encode proxy.");
						this.EndEncode(error: true);
					}
				}
			}
		}

		public void StopEncode()
		{
			lock (this.encoderLock)
			{
				if (this.channel != null)
				{
					try
					{
						this.channel.StopEncode();
					}
					catch (CommunicationException)
					{
						this.logger.LogError("Unable to contact encode proxy.");
						this.EndEncode(error: true);
					}
				}
			}
		}

		public void OnEncodeStarted()
		{
			this.IsEncodeStarted = true;
			if (this.EncodeStarted != null)
			{
				this.EncodeStarted(this, new EventArgs());
			}
		}

		public void OnEncodeProgress(float averageFrameRate, float currentFrameRate, TimeSpan estimatedTimeLeft, float fractionComplete, int pass)
		{
			lock (this.encoderLock)
			{
				if (this.encoding && this.EncodeProgress != null)
				{
					this.EncodeProgress(
						this,
						new EncodeProgressEventArgs
							{
								AverageFrameRate = averageFrameRate,
								CurrentFrameRate = currentFrameRate,
								EstimatedTimeLeft = estimatedTimeLeft,
								FractionComplete = fractionComplete,
								Pass = pass
							});
				}
			}
		}

		public void OnEncodeComplete(bool error)
		{
			lock (this.encoderLock)
			{
				this.EndEncode(error);
			}
		}

		public void OnMessageLogged(string message)
		{
			var entry = new LogEntry
			{
				LogType = LogType.Message,
				Source = LogSource.HandBrake,
				Text = message
			};

			this.logger.AddEntry(entry);
		}

		public void OnErrorLogged(string message)
		{
			var entry = new LogEntry
			{
				LogType = LogType.Error,
				Source = LogSource.HandBrake,
				Text = message
			};

			this.logger.AddEntry(entry);
		}

		public void OnException(string exceptionString)
		{
			this.logger.LogError("Encode worker crashed. Please report this error so it can be fixed in the future:" + Environment.NewLine + exceptionString);
		}

		private void EndEncode(bool error)
		{
			if (this.encoding)
			{
				if (this.EncodeCompleted != null)
				{
					this.EncodeCompleted(
						this,
						new EncodeCompletedEventArgs
							{
								Error = error
							});
				}

				this.encoding = false;
			}
		}
	}
}

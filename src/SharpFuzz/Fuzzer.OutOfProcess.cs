using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace SharpFuzz
{
	/// <summary>
	/// American fuzzy lop instrumentation and fork server for .NET libraries.
	/// </summary>
	public static partial class Fuzzer
	{
		private const string ControlHandle = "__SHARPFUZZ_CTL";
		private const string StatusHandle = "__SHARPFUZZ_ST";
		private const string ParentId = "__SHARPFUZZ_PID";
		private const string InitialRun = "__SHARPFUZZ_INIT";

		/// <summary>
		/// OutOfProccess class contains the special fork server implementation
		/// that can survive uncatchable exceptions and timeouts by executing
		/// the fuzzed code in the separate process. The child process will be
		/// automatically restarted after we detect that it's dead. Starting
		/// new dotnet process is very slow, so if you expect this situation
		/// to happen often, it's better to immediately fix the bugs causing
		/// it, and then continue with the fast fuzzing.
		/// </summary>
		public static class OutOfProcess
		{
			/// <summary>
			/// Run method starts the .NET equivalent of AFL fork server.
			/// It repeatedly executes the passed action and reports the
			/// execution result to afl-fuzz. If the executable that is
			/// calling it is not running under afl-fuzz, the action will
			/// be executed only once.
			/// </summary>
			/// <param name="action">
			/// Some action that calls the instrumented library. If an
			/// uncaught exception escapes the call, FAULT_CRASH execution
			/// status code is reported to afl-fuzz.
			/// </param>
			public static void Run(Action action)
			{
				ThrowIfNull(action, nameof(action));
				var s = Environment.GetEnvironmentVariable("__AFL_SHM_ID");

				if (s is null || !Int32.TryParse(s, out var shmid))
				{
					RunWithoutAflFuzz(action);
					return;
				}

				var ctl = Environment.GetEnvironmentVariable(ControlHandle);
				var st = Environment.GetEnvironmentVariable(StatusHandle);
				var pid = Environment.GetEnvironmentVariable(ParentId);
				var initial = Environment.GetEnvironmentVariable(InitialRun);

				if (ctl is null || st is null || pid is null || initial is null)
				{
					RunServer();
				}
				else
				{
					RunClient(action, shmid, ctl, st, Int32.Parse(pid), Boolean.Parse(initial));
				}
			}

			private static void RunServer()
			{
				var initial = true;

				using (var r = new BinaryReader(new AnonymousPipeClientStream(PipeDirection.In, "198")))
				using (var w = new BinaryWriter(new AnonymousPipeClientStream(PipeDirection.Out, "199")))
				{
					w.Write(0);

					while (true)
					{
						using (var ctlPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable))
						using (var stPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable))
						using (var ctl = new BinaryWriter(ctlPipe))
						using (var st = new BinaryReader(stPipe))
						{
							var info = new ProcessStartInfo("dotnet", Environment.CommandLine);

							info.Environment[ControlHandle] = ctlPipe.GetClientHandleAsString();
							info.Environment[StatusHandle] = stPipe.GetClientHandleAsString();
							info.Environment[ParentId] = Process.GetCurrentProcess().Id.ToString();
							info.Environment[InitialRun] = initial.ToString();

							var child = Process.Start(info);
							initial = false;

							ctlPipe.DisposeLocalCopyOfClientHandle();
							stPipe.DisposeLocalCopyOfClientHandle();

							while (true)
							{
								r.ReadInt32();
								w.Write(child.Id);
								int? fault = null;

								try
								{
									ctl.Write(0);
									fault = st.ReadInt32();
								}
								catch
								{
									// One of the pipes to the child process is broken.
									// It means that the child died, either because of
									// uncatchable exception, or because it timed out.
								}

								w.Write(fault ?? Fault.Crash);

								if (fault is null)
								{
									// The child process is dead, get out of the
									// fast loop and start the new child process.
									break;
								}
							}
						}
					}
				}
			}

			private static unsafe void RunClient(
				Action action,
				int shmid,
				string ctlHandle,
				string stHandle,
				int pid,
				bool initial)
			{
				// The only way to ensure that the child process is terminated
				// after the fork server is stopped is to monitor the parent
				// process, and exit early if we detect that the parent is dead.
				Task.Run(() => WaitForParent(pid));

				using (var shmaddr = Native.shmat(shmid, IntPtr.Zero, 0))
				using (var ctl = new BinaryReader(new AnonymousPipeClientStream(PipeDirection.In, ctlHandle)))
				using (var st = new BinaryWriter(new AnonymousPipeClientStream(PipeDirection.Out, stHandle)))
				{
					byte* sharedMem = (byte*)shmaddr.DangerousGetHandle();
					Common.Trace.SharedMem = sharedMem;

					// Unfortunately, we cannot ignore the first run each
					// time we start the new child process. If the previous
					// child was terminated because of the timeout, testing
					// the next input twice would almost certainly result
					// in the new timeout, but it will probably also produce
					// the different trace bits. That's why we do this only
					// as the part of the dry run.
					if (initial)
					{
						Setup(action, sharedMem);
					}

					while (true)
					{
						ctl.ReadInt32();
						st.Write(Execute(action));
					}
				}
			}

			private static void WaitForParent(int pid)
			{
				try
				{
					for (; ; )
					{
						var parent = Process.GetProcessById(pid);

						// On macOS, the parent process can sometimes be dead, but WaitForExit
						// doesn't detect that. In such situations, Process.GetProcessById will
						// return Process instance with the empty ProcessName property.
						if (String.IsNullOrEmpty(parent.ProcessName) || parent.WaitForExit(100))
						{
							Environment.Exit(1);
						}
					}
				}
				catch
				{
					// Parent process was killed before we even managed to start the child.
					Environment.Exit(1);
				}
			}
		}
	}
}

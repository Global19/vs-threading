﻿namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;

	[TestClass]
	public class AsyncPumpTests : TestBase {
		private AsyncPump asyncPump;
		private Thread originalThread;

		[TestInitialize]
		public void Initialize() {
			var ctxt = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(ctxt);
			this.asyncPump = new AsyncPump();
			this.originalThread = Thread.CurrentThread;
		}

		[TestCleanup]
		public void Cleanup() {
		}

		[TestMethod]
		public void RunActionSTA() {
			this.RunActionHelper();
		}

		[TestMethod]
		public void RunActionMTA() {
			Task.Run(() => this.RunActionHelper()).Wait();
		}

		[TestMethod]
		public void RunFuncOfTaskSTA() {
			this.RunFuncOfTaskHelper();
		}

		[TestMethod]
		public void RunFuncOfTaskMTA() {
			Task.Run(() => RunFuncOfTaskHelper()).Wait();
		}

		[TestMethod]
		public void RunFuncOfTaskOfTSTA() {
			RunFuncOfTaskOfTHelper();
		}

		[TestMethod]
		public void RunFuncOfTaskOfTMTA() {
			Task.Run(() => RunFuncOfTaskOfTHelper()).Wait();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LeaveAndReturnToSTA() {
			var fullyCompleted = false;
			this.asyncPump.RunSynchronously(async delegate {
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

				await this.asyncPump.SwitchToMainThreadAsync();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				fullyCompleted = true;
			});
			Assert.IsTrue(fullyCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToMainThreadDoesNotYieldWhenAlreadyOnMainThread() {
			Assert.IsTrue(this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().IsCompleted, "Yield occurred even when already on UI thread.");
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToMainThreadYieldsWhenOffMainThread() {
			Task.Run(
				() => Assert.IsFalse(this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().IsCompleted, "Yield did not occur when off Main thread."))
				.GetAwaiter().GetResult();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToSTADoesNotCauseUnrelatedReentrancy() {
			var frame = new DispatcherFrame();

			var uiThreadNowBusy = new TaskCompletionSource<object>();
			bool contenderHasReachedUIThread = false;

			var backgroundContender = Task.Run(async delegate {
				await uiThreadNowBusy.Task;
				await this.asyncPump.SwitchToMainThreadAsync();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				contenderHasReachedUIThread = true;
				frame.Continue = false;
			});

			this.asyncPump.RunSynchronously(async delegate {
				uiThreadNowBusy.SetResult(null);
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);
				await Task.Delay(AsyncDelay); // allow ample time for the background contender to re-enter the STA thread if it's possible (we don't want it to be).

				await this.asyncPump.SwitchToMainThreadAsync();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				Assert.IsFalse(contenderHasReachedUIThread, "The contender managed to get to the STA thread while other work was on it.");
			});

			// Pump messages until everything's done.
			Dispatcher.PushFrame(frame);

			Assert.IsTrue(backgroundContender.Wait(AsyncDelay), "Background contender never reached the UI thread.");
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToSTASucceedsForRelevantWork() {
			this.asyncPump.RunSynchronously(async delegate {
				var backgroundContender = Task.Run(async delegate {
					await this.asyncPump.SwitchToMainThreadAsync();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});

				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

				// We can't complete until this seemingly unrelated work completes.
				// This shouldn't deadlock because this synchronous operation kicked off
				// the operation to begin with.
				await backgroundContender;

				await this.asyncPump.SwitchToMainThreadAsync();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToSTASucceedsForDependentWork() {
			var uiThreadNowBusy = new TaskCompletionSource<object>();
			var backgroundContenderCompletedRelevantUIWork = new TaskCompletionSource<object>();
			var backgroundInvitationReverted = new TaskCompletionSource<object>();
			bool syncUIOperationCompleted = false;

			var backgroundContender = Task.Run(async delegate {
				await uiThreadNowBusy.Task;
				await this.asyncPump.SwitchToMainThreadAsync();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				// Release, then reacquire the STA a couple of different ways
				// to verify that even after the invitation has been extended
				// to join the STA thread we can leave and revisit.
				await this.asyncPump.SwitchToMainThreadAsync();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				// Now complete the task that the synchronous work is waiting before reverting their invitation.
				backgroundContenderCompletedRelevantUIWork.SetResult(null);

				// Temporarily get off UI thread until the UI thread has rescinded offer to lend its time.
				// In so doing, once the task we're waiting on has completed, we'll be scheduled to return using
				// the current synchronization context, which because we switched to the main thread earlier
				// and have not yet switched off, will mean our continuation won't execute until the UI thread
				// becomes available (without any reentrancy).
				await backgroundInvitationReverted.Task;

				// We should now be on the UI thread (and the Run delegate below should have altogether completd.)
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				Assert.IsTrue(syncUIOperationCompleted); // should be true because continuation needs same thread that this is set on.
			});

			this.asyncPump.RunSynchronously(async delegate {
				uiThreadNowBusy.SetResult(null);
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

				using (this.asyncPump.Join()) { // invite the work to re-enter our synchronous work on the STA thread.
					await backgroundContenderCompletedRelevantUIWork.Task; // we can't complete until this seemingly unrelated work completes.
				} // stop inviting more work from background thread.

				await this.asyncPump.SwitchToMainThreadAsync();
				var nowait = backgroundInvitationReverted.SetAsync();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				syncUIOperationCompleted = true;

				using (this.asyncPump.Join()) {
					// Since this background task finishes on the UI thread, we need to ensure
					// it can get on it.
					await backgroundContender;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyNestedNoJoins() {
			bool outerCompleted = false, innerCompleted = false;
			this.asyncPump.RunSynchronously(async delegate {
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await Task.Run(async delegate {
					await this.asyncPump.SwitchToMainThreadAsync();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});

				this.asyncPump.RunSynchronously(async delegate {
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
					await Task.Yield();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);

					await Task.Run(async delegate {
						await this.asyncPump.SwitchToMainThreadAsync();
						Assert.AreSame(this.originalThread, Thread.CurrentThread);
					});

					Assert.AreSame(this.originalThread, Thread.CurrentThread);
					innerCompleted = true;
				});

				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				outerCompleted = true;
			});

			Assert.IsTrue(innerCompleted, "Nested Run did not complete.");
			Assert.IsTrue(outerCompleted, "Outer Run did not complete.");
		}

		[TestMethod, Timeout(TestTimeout + AsyncDelay * 4)]
		public void RunSynchronouslyNestedWithJoins() {
			bool outerCompleted = false, innerCompleted = false;

			this.asyncPump.RunSynchronously(async delegate {
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await this.TestReentrancyOfUnrelatedDependentWork();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await Task.Run(async delegate {
					await this.asyncPump.SwitchToMainThreadAsync();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});

				await this.TestReentrancyOfUnrelatedDependentWork();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				this.asyncPump.RunSynchronously(async delegate {
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
					await Task.Yield();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);

					await this.TestReentrancyOfUnrelatedDependentWork();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);

					await Task.Run(async delegate {
						await this.asyncPump.SwitchToMainThreadAsync();
						Assert.AreSame(this.originalThread, Thread.CurrentThread);
					});

					await this.TestReentrancyOfUnrelatedDependentWork();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);

					Assert.AreSame(this.originalThread, Thread.CurrentThread);
					innerCompleted = true;
				});

				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				outerCompleted = true;
			});

			Assert.IsTrue(innerCompleted, "Nested Run did not complete.");
			Assert.IsTrue(outerCompleted, "Outer Run did not complete.");
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyOffMainThreadRequiresJoinToReenterMainThreadForSameAsyncPumpInstance() {
			var task = Task.Run(delegate {
				this.asyncPump.RunSynchronously(async delegate {
					await this.asyncPump.SwitchToMainThreadAsync();
					Assert.AreSame(this.originalThread, Thread.CurrentThread, "We're not on the Main thread!");
				});
			});

			this.asyncPump.RunSynchronously(async delegate {
				// Even though it's all the same instance of AsyncPump,
				// unrelated work (work not spun off from this block) must still be 
				// Joined in order to execute here.
				Assert.AreNotSame(task, await Task.WhenAny(task, Task.Delay(AsyncDelay / 2)), "The unrelated main thread work completed before the Main thread was joined.");
				using (this.asyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyOffMainThreadRequiresJoinToReenterMainThreadForDifferentAsyncPumpInstance() {
			var otherAsyncPump = new AsyncPump();
			var task = Task.Run(delegate {
				otherAsyncPump.RunSynchronously(async delegate {
					await otherAsyncPump.SwitchToMainThreadAsync();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});
			});

			this.asyncPump.RunSynchronously(async delegate {
				Assert.AreNotSame(task, await Task.WhenAny(task, Task.Delay(AsyncDelay / 2)), "The unrelated main thread work completed before the Main thread was joined.");
				using (otherAsyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void JoinRejectsSubsequentWork() {
			bool outerCompleted = false;

			var mainThreadDependentWorkQueued = new AsyncManualResetEvent();
			var dependentWorkCompleted = new AsyncManualResetEvent();
			var joinReverted = new AsyncManualResetEvent();
			var postJoinRevertedWorkQueued = new AsyncManualResetEvent();
			var postJoinRevertedWorkExecuting = new AsyncManualResetEvent();
			var unrelatedTask = Task.Run(async delegate {
				// STEP 2
				await this.asyncPump.SwitchToMainThreadAsync()
					.GetAwaiter().YieldAndNotify(mainThreadDependentWorkQueued);

				// STEP 4
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				dependentWorkCompleted.Set();
				await joinReverted.WaitAsync().ConfigureAwait(false);

				// STEP 6
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);
				await this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(postJoinRevertedWorkQueued, postJoinRevertedWorkExecuting);

				// STEP 8
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
			});

			this.asyncPump.RunSynchronously(async delegate {
				// STEP 1
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				await mainThreadDependentWorkQueued.WaitAsync();

				// STEP 3
				using (this.asyncPump.Join()) {
					await dependentWorkCompleted.WaitAsync();
				}

				// STEP 5
				joinReverted.Set();
				await postJoinRevertedWorkQueued.WaitAsync();

				// STEP 7
				var executingWaitTask = postJoinRevertedWorkExecuting.WaitAsync();
				Assert.AreNotSame(executingWaitTask, await Task.WhenAny(executingWaitTask, Task.Delay(AsyncDelay)), "Main thread work from unrelated task should not have executed.");

				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				outerCompleted = true;
			});

			Assert.IsTrue(outerCompleted, "Outer Run did not complete.");

			// Allow background task's last Main thread work to finish.
			Assert.IsFalse(unrelatedTask.IsCompleted);
			this.asyncPump.RunSynchronously(async delegate {
				using (this.asyncPump.Join()) {
					await unrelatedTask;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SyncContextRestoredAfterRun() {
			var syncContext = SynchronizationContext.Current;
			if (syncContext == null) {
				Assert.Inconclusive("We need a non-null sync context for this test to be useful.");
			}

			this.asyncPump.RunSynchronously(async delegate {
				await Task.Yield();
			});

			Assert.AreSame(syncContext, SynchronizationContext.Current);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void BackgroundSynchronousTransitionsToUIThreadSynchronous() {
			var task = Task.Run(delegate {
				this.asyncPump.RunSynchronously(async delegate {
					Assert.AreNotSame(this.originalThread, Thread.CurrentThread);
					await this.asyncPump.SwitchToMainThreadAsync();

					// The scenario here is that some code calls out, then back in, via a synchronous interface
					this.asyncPump.RunSynchronously(async delegate {
						await Task.Yield();
						await this.TestReentrancyOfUnrelatedDependentWork();
					});
				});
			});

			// Avoid a deadlock while waiting for test to complete.
			this.asyncPump.RunSynchronously(async delegate {
				using (this.asyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToMainThreadAwaiterReappliesAsyncLocalSyncContextOnContinuation() {
			var task = Task.Run(delegate {
				this.asyncPump.RunSynchronously(async delegate {
					Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

					// Switching to the main thread here will get us the SynchronizationContext we need,
					// and the awaiter's GetResult() should apply the AsyncLocal sync context as well
					// to avoid deadlocks later.
					await this.asyncPump.SwitchToMainThreadAsync();

					await this.TestReentrancyOfUnrelatedDependentWork();

					// The scenario here is that some code calls out, then back in, via a synchronous interface
					this.asyncPump.RunSynchronously(async delegate {
						await Task.Yield();
						await this.TestReentrancyOfUnrelatedDependentWork();
					});
				});
			});

			// Avoid a deadlock while waiting for test to complete.
			this.asyncPump.RunSynchronously(async delegate {
				using (this.asyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NestedJoinsDistinctAsyncPumps() {
			const int nestLevels = 3;
			MockAsyncService outerService = null;
			for (int level = 0; level < nestLevels; level++) {
				outerService = new MockAsyncService(outerService);
			}

			var operationTask = outerService.OperationAsync();

			this.asyncPump.RunSynchronously(async delegate {
				await outerService.StopAsync(operationTask);
			});

			Assert.IsTrue(operationTask.IsCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyKicksOffReturnsThenSyncBlocksStillRequiresJoin() {
			var mainThreadNowBlocking = new AsyncManualResetEvent();
			Task task = null;
			this.asyncPump.RunSynchronously(delegate {
				task = Task.Run(async delegate {
					await mainThreadNowBlocking.WaitAsync();
					await this.asyncPump.SwitchToMainThreadAsync();
				});
			});

			this.asyncPump.RunSynchronously(async delegate {
				mainThreadNowBlocking.Set();
				Assert.AreNotSame(task, await Task.WhenAny(task, Task.Delay(AsyncDelay / 2)));
				using (this.asyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void KickOffAsyncWorkFromMainThreadThenBlockOnIt() {
			Task task = null;
			this.asyncPump.RunSynchronously(delegate {
				task = this.SomeOperationThatMayBeOnMainThreadAsync();
			});

			this.asyncPump.RunSynchronously(async delegate {
				using (this.asyncPump.Join()) {
					await task;
				}
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void MainThreadTaskScheduler() {
			this.asyncPump.RunSynchronously(async delegate {
				bool completed = false;
				await Task.Factory.StartNew(
					() => {
						Assert.AreSame(this.originalThread, Thread.CurrentThread);
						completed = true;
					},
					CancellationToken.None,
					TaskCreationOptions.None,
					this.asyncPump.MainThreadTaskScheduler);
				Assert.IsTrue(completed);
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void RunSynchronouslyTaskOfTWithFireAndForgetMethod() {
			this.asyncPump.RunSynchronously(async delegate {
				await Task.Yield();
				SomeFireAndForgetMethod();
				await Task.Yield();
				await Task.Delay(AsyncDelay);
			});
		}

		private static async void SomeFireAndForgetMethod() {
			await Task.Yield();
		}

		private async Task SomeOperationThatMayBeOnMainThreadAsync() {
			await Task.Yield();
			await Task.Yield();
		}

		private async Task TestReentrancyOfUnrelatedDependentWork() {
			var unrelatedMainThreadWorkWaiting = new AsyncManualResetEvent();
			var unrelatedMainThreadWorkInvoked = new AsyncManualResetEvent();
			AsyncPump unrelatedPump;
			Task unrelatedTask;

			// don't let this task be identified as related to the caller, so that the caller has to Join for this to complete.
			using (this.asyncPump.SuppressRelevance()) {
				unrelatedPump = new AsyncPump();
				unrelatedTask = Task.Run(async delegate {
					await unrelatedPump.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(unrelatedMainThreadWorkWaiting, unrelatedMainThreadWorkInvoked);
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});
			}

			await unrelatedMainThreadWorkWaiting.WaitAsync();

			// Await an extra bit of time to allow for unexpected reentrancy to occur while the
			// main thread is only synchronously blocking.
			var waitTask = unrelatedMainThreadWorkInvoked.WaitAsync();
			Assert.AreNotSame(
				waitTask,
				await Task.WhenAny(waitTask, Task.Delay(AsyncDelay / 2)),
				"Background work completed work on the UI thread before it was invited to do so.");

			using (unrelatedPump.Join()) {
				// The work SHOULD be able to complete now that we've Joined the work.
				await waitTask;
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
			}
		}

		private void RunActionHelper() {
			var initialThread = Thread.CurrentThread;
			this.asyncPump.RunSynchronously((Action)async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
			});
		}

		private void RunFuncOfTaskHelper() {
			var initialThread = Thread.CurrentThread;
			this.asyncPump.RunSynchronously(async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
			});
		}

		private void RunFuncOfTaskOfTHelper() {
			var initialThread = Thread.CurrentThread;
			var expectedResult = new GenericParameterHelper();
			GenericParameterHelper actualResult = this.asyncPump.RunSynchronously(async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
				return expectedResult;
			});
			Assert.AreSame(expectedResult, actualResult);
		}

		private class MockAsyncService {
			private AsyncPump pump = new AsyncPump();
			private AsyncManualResetEvent stopRequested = new AsyncManualResetEvent();
			private Thread originalThread = Thread.CurrentThread;
			private Task dependentTask;
			private MockAsyncService dependentService;

			internal MockAsyncService(MockAsyncService dependentService = null) {
				this.dependentService = dependentService;
			}

			internal async Task OperationAsync() {
				await this.pump.SwitchToMainThreadAsync();
				if (this.dependentService != null) {
					await (this.dependentTask = this.dependentService.OperationAsync());
				}

				await this.stopRequested.WaitAsync();
				await Task.Yield();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
			}

			internal async Task StopAsync(Task operation) {
				Requires.NotNull(operation, "operation");
				if (this.dependentService != null) {
					await this.dependentService.StopAsync(this.dependentTask);
				}

				this.stopRequested.Set();
				using (this.pump.Join()) {
					await operation;
				}
			}
		}
	}
}
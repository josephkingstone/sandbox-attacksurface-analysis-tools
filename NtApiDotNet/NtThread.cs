﻿//  Copyright 2016 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace NtApiDotNet
{
    /// <summary>
    /// Class to represent a NT Thread object
    /// </summary>
    [NtType("Thread")]
    public class NtThread : NtObjectWithDuplicateAndInfo<NtThread, ThreadAccessRights, ThreadInformationClass, ThreadInformationClass>
    {
        #region Private Members
        private int? _tid;
        private int? _pid;
        private string _process_name;

        private static NtResult<NtThread> Open(NtThreadInformation thread_info, ThreadAccessRights desired_access, bool throw_on_error)
        {
            var result = Open(thread_info.ThreadId, desired_access, throw_on_error);
            if (result.IsSuccess)
            {
                result.Result._process_name = thread_info.ProcessName;
            }
            return result;
        }

        private ThreadBasicInformation QueryBasicInformation()
        {
            return Query<ThreadBasicInformation>(ThreadInformationClass.ThreadBasicInformation);
        }

        private IContext GetX86Context(ContextFlags flags)
        {
            var context = new ContextX86();
            context.ContextFlags = flags;

            using (var buffer = context.ToBuffer())
            {
                NtSystemCalls.NtGetContextThread(Handle, buffer).ToNtException();
                return buffer.Result;
            }
        }

        private IContext GetAmd64Context(ContextFlags flags)
        {
            var context = new ContextAmd64();
            context.ContextFlags = flags;

            // Buffer needs to be 16 bytes aligned, so allocate some extract space in case.
            using (var buffer = new SafeHGlobalBuffer(Marshal.SizeOf(context) + 16))
            {
                int write_ofs = 0;
                long ptr = buffer.DangerousGetHandle().ToInt64();
                // Almost certainly 8 byte aligned, but just in case.
                if ((ptr & 0xF) != 0)
                {
                    write_ofs = (int)(0x10 - (ptr & 0xF));
                }

                Marshal.StructureToPtr(context, buffer.DangerousGetHandle() + write_ofs, false);
                var sbuffer = buffer.GetStructAtOffset<ContextAmd64>(write_ofs);
                NtSystemCalls.NtGetContextThread(Handle, sbuffer).ToNtException();
                return sbuffer.Result;
            }
        }

        #endregion

        #region Constructors
        internal NtThread(SafeKernelObjectHandle handle)
            : base(handle)
        {
        }

        internal sealed class NtTypeFactoryImpl : NtTypeFactoryImplBase
        {
            public NtTypeFactoryImpl() : base(false)
            {
            }
        }
        #endregion

        #region Static Methods

        /// <summary>
        /// Open a thread
        /// </summary>
        /// <param name="thread_id">The thread ID to open</param>
        /// <param name="desired_access">The desired access for the handle</param>
        /// <param name="throw_on_error">True to throw an exception on error.</param>
        /// <returns>The NT status code and object result.</returns>
        public static NtResult<NtThread> Open(int thread_id, ThreadAccessRights desired_access, bool throw_on_error)
        {
            return NtSystemCalls.NtOpenThread(out SafeKernelObjectHandle handle, desired_access, new ObjectAttributes(),
                new ClientId() { UniqueThread = new IntPtr(thread_id) }).CreateResult(throw_on_error, () => new NtThread(handle) { _tid = thread_id });
        }

        /// <summary>
        /// Open a thread
        /// </summary>
        /// <param name="thread_id">The thread ID to open</param>
        /// <param name="desired_access">The desired access for the handle</param>
        /// <returns>The opened object</returns>
        public static NtThread Open(int thread_id, ThreadAccessRights desired_access)
        {
            return Open(thread_id, desired_access, true).Result;
        }

        /// <summary>
        /// Gets all accessible threads on the system.
        /// </summary>
        /// <param name="desired_access">The desired access for each thread.</param>
        /// <param name="from_system_info">Get the thread list from system information.</param>
        /// <returns>The list of accessible threads.</returns>
        public static IEnumerable<NtThread> GetThreads(ThreadAccessRights desired_access, bool from_system_info)
        {
            if (from_system_info)
            {
                return NtSystemInfo.GetProcessInformation().SelectMany(p => p.Threads)
                    .Select(t => Open(t, desired_access, false)).SelectValidResults();
            }
            else
            {
                using (var threads = new DisposableList<NtThread>())
                {
                    using (var procs = NtProcess.GetProcesses(ProcessAccessRights.QueryInformation).ToDisposableList())
                    {
                        foreach (var proc in procs)
                        {
                            threads.AddRange(proc.GetThreads(desired_access));
                        }
                    }
                    return threads.ToArrayAndClear();
                }
            }
        }

        /// <summary>
        /// Gets all accessible threads on the system.
        /// </summary>
        /// <param name="desired_access">The desired access for each thread.</param>
        /// <returns>The list of accessible threads.</returns>
        public static IEnumerable<NtThread> GetThreads(ThreadAccessRights desired_access)
        {
            return GetThreads(desired_access, false);
        }

        /// <summary>
        /// Get first thread for process.
        /// </summary>
        /// <param name="process">The process handle to get the threads.</param>
        /// <param name="desired_access">The desired access for the thread.</param>
        /// <returns>The first thread, or null if no more available.</returns>
        public static NtThread GetFirstThread(NtProcess process, ThreadAccessRights desired_access)
        {
            NtStatus status = NtSystemCalls.NtGetNextThread(
                process.Handle, SafeKernelObjectHandle.Null, desired_access,
                AttributeFlags.None, 0, out SafeKernelObjectHandle new_handle);
            if (status == NtStatus.STATUS_SUCCESS)
            {
                return new NtThread(new_handle);
            }
            return null;
        }

        /// <summary>
        /// Sleep the current thread
        /// </summary>
        /// <param name="alertable">Set if the thread should be alertable</param>
        /// <param name="delay">The delay, negative values indicate relative times.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>STATUS_ALERTED if the thread was alerted, other success or error code.</returns>
        public static NtStatus Sleep(bool alertable, NtWaitTimeout delay, bool throw_on_error)
        {
            return NtSystemCalls.NtDelayExecution(alertable, delay?.Timeout).ToNtException(throw_on_error);
        }

        /// <summary>
        /// Sleep the current thread
        /// </summary>
        /// <param name="alertable">Set if the thread should be alertable</param>
        /// <param name="delay">The delay, negative values indicate relative times.</param>
        /// <returns>True if the thread was alerted before the delay expired.</returns>
        public static bool Sleep(bool alertable, NtWaitTimeout delay)
        {
            return Sleep(alertable, delay, true) == NtStatus.STATUS_ALERTED;
        }

        /// <summary>
        /// Sleep the current thread
        /// </summary>
        /// <param name="alertable">Set if the thread should be alertable</param>
        /// <param name="delay">The delay, negative values indicate relative times.</param>
        /// <returns>True if the thread was alerted before the delay expired.</returns>
        public static bool Sleep(bool alertable, long delay)
        {
            return Sleep(alertable, new NtWaitTimeout(delay));
        }

        /// <summary>
        /// Sleep the current thread for a specified number of milliseconds.
        /// </summary>
        /// <param name="delay_ms">The delay in milliseconds.</param>
        /// <returns>True if the thread was alerted before the delay expired.</returns>
        public static bool SleepMs(long delay_ms)
        {
            return Sleep(false, NtWaitTimeout.FromMilliseconds(delay_ms));
        }

        /// <summary>
        /// Open an actual handle to the current thread rather than the pseudo one used for Current
        /// </summary>
        /// <returns>The thread object</returns>
        public static NtThread OpenCurrent()
        {
            return Current.Duplicate();
        }

        #endregion

        #region Static Properties

        /// <summary>
        /// Get the current thread.
        /// </summary>
        /// <remarks>This only uses the pseudo handle, for the thread. You can't use it in different threads. If you need to do that use OpenCurrent.</remarks>
        /// <see cref="OpenCurrent"/>
        public static NtThread Current { get { return new NtThread(new SafeKernelObjectHandle(new IntPtr(-2), false)); } }

        #endregion

        #region Public Methods

        /// <summary>
        /// Resume the thread.
        /// </summary>
        /// <returns>The suspend count</returns>
        public int Resume()
        {
            NtSystemCalls.NtResumeThread(Handle, out int suspend_count).ToNtException();
            return suspend_count;
        }

        /// <summary>
        /// Suspend the thread
        /// </summary>
        /// <returns>The suspend count</returns>
        public int Suspend()
        {
            NtSystemCalls.NtSuspendThread(Handle, out int suspend_count).ToNtException();
            return suspend_count;
        }

        /// <summary>
        /// Terminate the thread
        /// </summary>
        /// <param name="status">The thread status exit code</param>
        public void Terminate(NtStatus status)
        {
            NtSystemCalls.NtTerminateThread(Handle, status).ToNtException();
        }

        /// <summary>
        /// Wake the thread from an alertable state.
        /// </summary>
        public void Alert()
        {
            NtSystemCalls.NtAlertThread(Handle).ToNtException();
        }

        /// <summary>
        /// Wake the thread from an alertable state and resume the thread.
        /// </summary>
        /// <returns>The previous suspend count for the thread.</returns>
        public int AlertResume()
        {
            OptionalInt32 suspend_count = new OptionalInt32();
            NtSystemCalls.NtAlertResumeThread(Handle, suspend_count).ToNtException();
            return suspend_count.Value;
        }

        /// <summary>
        /// Hide the thread from debug events.
        /// </summary>
        public void HideFromDebugger()
        {
            NtSystemCalls.NtSetInformationThread(Handle, ThreadInformationClass.ThreadHideFromDebugger, SafeHGlobalBuffer.Null, 0).ToNtException();
        }

        /// <summary>
        /// The set the thread's impersonation token
        /// </summary>
        /// <param name="token">The impersonation token to set</param>
        public void SetImpersonationToken(NtToken token)
        {
            IntPtr handle = token != null ? token.Handle.DangerousGetHandle() : IntPtr.Zero;
            using (var buf = handle.ToBuffer())
            {
                NtSystemCalls.NtSetInformationThread(Handle, ThreadInformationClass.ThreadImpersonationToken,
                    buf, buf.Length).ToNtException();
            }
        }

        /// <summary>
        /// Impersonate the anonymous token
        /// </summary>
        /// <returns>The impersonation context. Dispose to revert to self</returns>
        public ThreadImpersonationContext ImpersonateAnonymousToken()
        {
            NtSystemCalls.NtImpersonateAnonymousToken(Handle).ToNtException();
            return new ThreadImpersonationContext(Duplicate());
        }

        /// <summary>
        /// Impersonate a token
        /// </summary>
        /// <returns>The impersonation context. Dispose to revert to self</returns>
        public ThreadImpersonationContext Impersonate(NtToken token)
        {
            SetImpersonationToken(token);
            return new ThreadImpersonationContext(Duplicate());
        }

        /// <summary>
        /// Impersonate another thread.
        /// </summary>
        /// <param name="thread">The thread to impersonate.</param>
        /// <param name="impersonation_level">The impersonation level</param>
        /// <returns>The imperonsation context. Dispose to revert to self.</returns>
        public ThreadImpersonationContext ImpersonateThread(NtThread thread, SecurityImpersonationLevel impersonation_level)
        {
            NtSystemCalls.NtImpersonateThread(Handle, thread.Handle,
                new SecurityQualityOfService(impersonation_level, SecurityContextTrackingMode.Static, false)).ToNtException();
            return new ThreadImpersonationContext(Duplicate());
        }

        /// <summary>
        /// Impersonate another thread.
        /// </summary>
        /// <param name="thread">The thread to impersonate.</param>
        /// <returns>The imperonsation context. Dispose to revert to self.</returns>
        public ThreadImpersonationContext ImpersonateThread(NtThread thread)
        {
            return ImpersonateThread(thread, SecurityImpersonationLevel.Impersonation);
        }

        /// <summary>
        /// Open the thread's token
        /// </summary>
        /// <returns>The token, null if no token available</returns>
        public NtToken OpenToken()
        {
            return NtToken.OpenThreadToken(this);
        }

        /// <summary>
        /// Queue a user APC to the thread.
        /// </summary>
        /// <param name="apc_routine">The APC callback pointer.</param>
        /// <param name="normal_context">Context parameter.</param>
        /// <param name="system_argument1">System argument 1.</param>
        /// <param name="system_argument2">System argument 2.</param>
        public void QueueUserApc(IntPtr apc_routine, IntPtr normal_context, IntPtr system_argument1, IntPtr system_argument2)
        {
            NtSystemCalls.NtQueueApcThread(Handle, apc_routine, normal_context, system_argument1, system_argument2).ToNtException();
        }

        /// <summary>
        /// Queue a user APC to the thread.
        /// </summary>
        /// <param name="apc_routine">The APC callback delegate.</param>
        /// <param name="normal_context">Context parameter.</param>
        /// <param name="system_argument1">System argument 1.</param>
        /// <param name="system_argument2">System argument 2.</param>
        /// <remarks>This is only for APCs in the current process. You also must ensure the delegate is
        /// valid at all times as this method doesn't take a reference to the delegate to prevent it being
        /// garbage collected.</remarks>
        public void QueueUserApc(ApcCallback apc_routine, IntPtr normal_context, IntPtr system_argument1, IntPtr system_argument2)
        {
            NtSystemCalls.NtQueueApcThread(Handle, Marshal.GetFunctionPointerForDelegate(apc_routine),
                normal_context, system_argument1, system_argument2).ToNtException();
        }

        /// <summary>
        /// Get next thread for process relative to current thread.
        /// </summary>
        /// <param name="process">The process handle to get the threads.</param>
        /// <param name="desired_access">The desired access for the thread.</param>
        /// <returns>The next thread, or null if no more available.</returns>
        public NtThread GetNextThread(NtProcess process, ThreadAccessRights desired_access)
        {
            NtStatus status = NtSystemCalls.NtGetNextThread(
                process.Handle, Handle, desired_access,
                AttributeFlags.None, 0, out SafeKernelObjectHandle new_handle);
            if (status == NtStatus.STATUS_SUCCESS)
            {
                return new NtThread(new_handle);
            }
            return null;
        }

        /// <summary>
        /// Get the thread context.
        /// </summary>
        /// <param name="flags">Flags for context parts to get.</param>
        /// <returns>An instance of an IContext object. Needs to be cast to correct type to access.</returns>
        public IContext GetContext(ContextFlags flags)
        {
            // Really needs to support ARM as well.
            if (Environment.Is64BitProcess)
            {
                return GetAmd64Context(flags);
            }
            else
            {
                return GetX86Context(flags);
            }
        }

        /// <summary>
        /// Get current waiting server information.
        /// </summary>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The thread ALPC server information.</returns>
        public NtResult<ThreadAlpcServerInformation> GetAlpcServerInformation(bool throw_on_error)
        {
            AlpcServerInformation info = new AlpcServerInformation() { ThreadHandle = Handle.DangerousGetHandle() };
            using (var buffer = info.ToBuffer(1024, true))
            {
                return NtSystemCalls.NtAlpcQueryInformation(SafeKernelObjectHandle.Null, AlpcPortInformationClass.AlpcServerInformation,
                    buffer, buffer.Length, out int return_length).CreateResult(throw_on_error, () => new ThreadAlpcServerInformation(buffer.Result.Out));
            }
        }

        /// <summary>
        /// Get current waiting server information.
        /// </summary>
        /// <returns>The thread ALPC server information.</returns>
        public ThreadAlpcServerInformation GetAlpcServerInformation()
        {
            return GetAlpcServerInformation(true).Result;
        }

        /// <summary>
        /// Method to query information for this object type.
        /// </summary>
        /// <param name="info_class">The information class.</param>
        /// <param name="buffer">The buffer to return data in.</param>
        /// <param name="return_length">Return length from the query.</param>
        /// <returns>The NT status code for the query.</returns>
        public override NtStatus QueryInformation(ThreadInformationClass info_class, SafeBuffer buffer, out int return_length)
        {
            return NtSystemCalls.NtQueryInformationThread(Handle, info_class, buffer, buffer.GetLength(), out return_length);
        }

        /// <summary>
        /// Method to set information for this object type.
        /// </summary>
        /// <param name="info_class">The information class.</param>
        /// <param name="buffer">The buffer to set data from.</param>
        /// <returns>The NT status code for the set.</returns>
        public override NtStatus SetInformation(ThreadInformationClass info_class, SafeBuffer buffer)
        {
            return NtSystemCalls.NtSetInformationThread(Handle, info_class, buffer, buffer.GetLength());
        }

        #endregion

        #region Public Properties
        /// <summary>
        /// Get thread ID
        /// </summary>
        public int ThreadId
        {
            get
            {
                if (!_tid.HasValue)
                {
                    _tid = QueryBasicInformation().ClientId.UniqueThread.ToInt32();
                }
                return _tid.Value;
            }
        }

        /// <summary>
        /// Get process ID
        /// </summary>
        public int ProcessId
        {
            get
            {
                if (!_pid.HasValue)
                {
                    _pid = QueryBasicInformation().ClientId.UniqueProcess.ToInt32();
                }
                return _pid.Value;
            }
        }

        /// <summary>
        /// Get name of process.
        /// </summary>
        public string ProcessName
        {
            get
            {
                if (_process_name == null)
                {
                    using (var proc = NtProcess.Open(ProcessId, ProcessAccessRights.QueryLimitedInformation, false))
                    {
                        if (proc.IsSuccess)
                        {
                            _process_name = proc.Result.Name;
                        }
                        else
                        {
                            _process_name = string.Empty;
                        }
                    }
                }
                return _process_name;
            }
        }

        /// <summary>
        /// Get thread's current priority
        /// </summary>
        public int Priority
        {
            get
            {
                return QueryBasicInformation().Priority;
            }
        }

        /// <summary>
        /// Get thread's base priority
        /// </summary>
        public int BasePriority
        {
            get
            {
                return QueryBasicInformation().BasePriority;
            }
        }

        /// <summary>
        /// Get the thread's TEB base address.
        /// </summary>
        public IntPtr TebBaseAddress
        {
            get
            {
                return QueryBasicInformation().TebBaseAddress;
            }
        }

        /// <summary>
        /// Get whether thread is allowed to create dynamic code.
        /// </summary>
        public bool AllowDynamicCode
        {
            get
            {
                return Query<int>(ThreadInformationClass.ThreadDynamicCodePolicyInfo) != 0;
            }
        }

        /// <summary>
        /// Get whether thread is impersonating another token.
        /// </summary>
        /// <remarks>Note that this tries to open the thread's token and return true if it could open. A return of false
        /// might just indicate that the caller doesn't have permission to open the token, not that it's not impersonating.</remarks>
        public bool Impersonating
        {
            get { try { using (var token = OpenToken()) { return token != null; } } catch { return false; } }
        }

        /// <summary>
        /// Get name of the thread.
        /// </summary>
        public override string FullPath
        {
            get
            {
                try
                {
                    string description = Description;
                    if (string.IsNullOrEmpty(description))
                    {
                        return $"thread:{ThreadId} - process:{ProcessId}";
                    }
                    return description;
                }
                catch
                {
                    return "Unknown";
                }
            }
        }

        /// <summary>
        /// Get or set a thread's description.
        /// </summary>
        public string Description
        {
            get
            {
                using (var buffer = QueryBuffer(ThreadInformationClass.ThreadNameInformation, new UnicodeStringOut(), false))
                {
                    if (buffer.IsSuccess)
                    {
                        return buffer.Result.Result.ToString();
                    }
                    return string.Empty;
                }
            }

            set
            {
                Set(ThreadInformationClass.ThreadNameInformation, new UnicodeStringIn(value));
            }
        }

        /// <summary>
        /// Get the Win32 start address for the thread.
        /// </summary>
        public long Win32StartAddress
        {
            get { return Query<IntPtr>(ThreadInformationClass.ThreadQuerySetWin32StartAddress).ToInt64(); }
        }

        /// <summary>
        /// Get last system call on the thread.
        /// </summary>
        public ThreadLastSystemCall LastSystemCall
        {
            get
            {
                var result = Query(ThreadInformationClass.ThreadLastSystemCall, new ThreadLastSystemCallExtendedInformation(), false);
                if (result.IsSuccess)
                {
                    return new ThreadLastSystemCall(result.Result);
                }

                if (result.Status == NtStatus.STATUS_INFO_LENGTH_MISMATCH)
                {
                    return new ThreadLastSystemCall(Query<ThreadLastSystemCallInformation>(ThreadInformationClass.ThreadLastSystemCall));
                }

                throw new NtException(result.Status);
            }
        }

        #endregion
    }
}

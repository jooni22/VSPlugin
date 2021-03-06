﻿//* Copyright 2010-2011 Research In Motion Limited.
//*
//* Licensed under the Apache License, Version 2.0 (the "License");
//* you may not use this file except in compliance with the License.
//* You may obtain a copy of the License at
//*
//* http://www.apache.org/licenses/LICENSE-2.0
//*
//* Unless required by applicable law or agreed to in writing, software
//* distributed under the License is distributed on an "AS IS" BASIS,
//* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//* See the License for the specific language governing permissions and
//* limitations under the License.

using System;
using System.Collections.Generic;
using BlackBerry.NativeCore.Debugger.Model;
using Microsoft.VisualStudio.Debugger.Interop;

namespace BlackBerry.DebugEngine
{
    /// <summary>
    /// This class manages breakpoints for the engine. 
    /// </summary>
    public sealed class BreakpointManager
    {
        /// <summary>
        /// The AD7Engine object that represents the DE.
        /// </summary>
        private readonly AD7Engine _engine;

        /// <summary>
        /// List of pending breakpoints.
        /// </summary>
        private readonly List<AD7PendingBreakpoint> _pendingBreakpoints;

        /// <summary>
        /// List of active breakpoints.
        /// </summary>
        private readonly List<AD7BoundBreakpoint> _activeBPs;

        /// <summary>
        /// Breakpoint manager constructor.
        /// </summary>
        /// <param name="engine"> Associated Debug Engine. </param>
        public BreakpointManager(AD7Engine engine)
        {
            if (engine == null)
                throw new ArgumentNullException("engine");

            _engine = engine;
            _pendingBreakpoints = new List<AD7PendingBreakpoint>();
            _activeBPs = new List<AD7BoundBreakpoint>();
        }

        /// <summary>
        /// A helper method used to construct a new pending breakpoint.
        /// </summary>
        /// <param name="request"> An IDebugBreakpointRequest2 object that describes the pending breakpoint to create. </param>
        /// <param name="ppPendingBreakpoint"> Returns an IDebugPendingBreakpoint2 object that represents the pending breakpoint. </param>
        public void CreatePendingBreakpoint(IDebugBreakpointRequest2 request, out IDebugPendingBreakpoint2 ppPendingBreakpoint)
        {
            AD7PendingBreakpoint pendingBreakpoint = new AD7PendingBreakpoint(_engine, request);
            ppPendingBreakpoint = pendingBreakpoint;
            _pendingBreakpoints.Add(pendingBreakpoint);
        }

        /// <summary>
        /// Return the active bound breakpoint matching the given GDB ID.
        /// </summary>
        /// <param name="gdbID"> Breakpoint ID in GDB. </param>
        /// <returns> If successful, returns the active bound breakpoint; otherwise, returns null. </returns>
        public AD7BoundBreakpoint GetBoundBreakpointForGDBID(uint gdbID)
        {
            foreach (AD7BoundBreakpoint breakpoint in _activeBPs)
            {
                if (breakpoint != null && breakpoint.GdbInfo.ID == gdbID)
                {
                    return breakpoint;
                }
            }
            return null;
        }

        /// <summary>
        /// Called from the engine's detach method to remove the debugger's breakpoint instructions.
        /// </summary>
        public void ClearBoundBreakpoints()
        {
            foreach (AD7PendingBreakpoint breakpoint in _pendingBreakpoints)
            {
                breakpoint.ClearBoundBreakpoints();
            }
        }

        /// <summary>
        /// Creates an entry and remotely enables the breakpoint in the debug stub.
        /// </summary>
        /// <param name="breakpoint">The bound breakpoint to add.</param>
        /// <param name="info">Info about GDB breakpoint.</param>
        public void RemoteAdd(AD7BoundBreakpoint breakpoint, out BreakpointInfo info)
        {
            // Call GDB to set a breakpoint based on filename and line no. in aBBP
            bool ret = false;

            info = null;
            if (breakpoint.m_bpLocationType == (uint)enum_BP_LOCATION_TYPE.BPLT_CODE_FILE_LINE)
            {
                ret = _engine.EventDispatcher.SetBreakpoint(breakpoint.m_filename, breakpoint.m_line, out info);
            }
            else if (breakpoint.m_bpLocationType == (uint)enum_BP_LOCATION_TYPE.BPLT_CODE_FUNC_OFFSET)
            {
                ret = _engine.EventDispatcher.SetBreakpoint(breakpoint.m_func, out info);
            }

            if (ret && info != null)
            {
                _activeBPs.Add(breakpoint);
            }
        }

        /// <summary>
        /// Enable bound breakpoint.
        /// </summary>
        /// <param name="breakpoint"> The Bound breakpoint to enable. </param>
        public void RemoteEnable(AD7BoundBreakpoint breakpoint)
        {
            _engine.EventDispatcher.EnableBreakpoint(breakpoint.GdbInfo.ID, true);
        }

        /// <summary>
        /// Disable bound breakpoint.
        /// </summary>
        /// <param name="breakpoint"> The Bound breakpoint to disable. </param>
        public void RemoteDisable(AD7BoundBreakpoint breakpoint)
        {
            _engine.EventDispatcher.EnableBreakpoint(breakpoint.GdbInfo.ID, false);
        }

        /// <summary>
        /// Remove the associated bound breakpoint.
        /// </summary>
        /// <param name="breakpoint"> The breakpoint to remove. </param>
        public void RemoteDelete(AD7BoundBreakpoint breakpoint)
        {
            _activeBPs.Remove(breakpoint);
            _engine.EventDispatcher.DeleteBreakpoint(breakpoint.GdbInfo.ID);
        }
    }
}

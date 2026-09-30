const DEFINITIONS = {
	remaining: { name: 'Remaining time (MM:SS, "-" when over)' },
	remaining_seconds: { name: 'Remaining seconds (negative when over)' },
	duration: { name: 'Duration (MM:SS)' },
	duration_seconds: { name: 'Duration in seconds' },
	progress_percent: { name: 'Remaining as % of duration' },
	phase: { name: 'Phase (normal / warning / critical / expired)' },
	status: { name: 'Status (running / paused / offline)' },
	running: { name: 'Running (true / false)' },
	overlay_visible: { name: 'Overlay visible (true / false)' },
	presenter_view: { name: 'Presenter view detected (true / false)' },
}

export function UpdateVariableDefinitions(self) {
	self.setVariableDefinitions(DEFINITIONS)
}

export function variableValuesFromState(state) {
	if (!state) {
		return {
			remaining: '--:--',
			remaining_seconds: undefined,
			duration: '--:--',
			duration_seconds: undefined,
			progress_percent: undefined,
			phase: 'offline',
			status: 'offline',
			running: false,
			overlay_visible: false,
			presenter_view: false,
		}
	}
	return {
		remaining: state.display,
		remaining_seconds: state.remainingSeconds,
		duration: state.duration,
		duration_seconds: Math.round(state.durationMs / 1000),
		progress_percent: Math.round(state.progress * 100),
		phase: state.phase,
		status: state.running ? 'running' : 'paused',
		running: state.running,
		overlay_visible: state.visible,
		presenter_view: state.presenterView,
	}
}

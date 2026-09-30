import { combineRgb } from '@companion-module/base'
import { FEATURES } from './actions.js'

export function UpdateFeedbacks(self) {
	self.setFeedbackDefinitions({
		phase: {
			type: 'boolean',
			name: 'Timer phase',
			description: 'normal → warning (amber) → critical (red) → expired (00:00 / overtime)',
			defaultStyle: {
				bgcolor: combineRgb(200, 30, 30),
				color: combineRgb(255, 255, 255),
			},
			options: [
				{
					type: 'dropdown',
					id: 'phase',
					label: 'Phase',
					default: 'critical',
					choices: [
						{ id: 'normal', label: 'Normal' },
						{ id: 'warning', label: 'Warning' },
						{ id: 'critical', label: 'Critical' },
						{ id: 'expired', label: 'Expired (00:00 / overtime)' },
					],
				},
			],
			callback: ({ options }) => self.state?.phase === options.phase,
		},

		running: {
			type: 'boolean',
			name: 'Timer running',
			defaultStyle: {
				bgcolor: combineRgb(0, 150, 0),
				color: combineRgb(255, 255, 255),
			},
			options: [],
			callback: () => self.state?.running === true,
		},

		paused: {
			type: 'boolean',
			name: 'Timer paused',
			defaultStyle: {
				color: combineRgb(150, 150, 150),
			},
			options: [],
			callback: () => self.state?.running === false,
		},

		overlay_visible: {
			type: 'boolean',
			name: 'Overlay visible',
			defaultStyle: {
				bgcolor: combineRgb(0, 90, 170),
				color: combineRgb(255, 255, 255),
			},
			options: [],
			callback: () => self.state?.visible === true,
		},

		presenter_view: {
			type: 'boolean',
			name: 'Presenter view detected',
			description: 'True while PowerPoint is in a slide show with presenter view open',
			defaultStyle: {
				bgcolor: combineRgb(0, 120, 60),
				color: combineRgb(255, 255, 255),
			},
			options: [],
			callback: () => self.state?.presenterView === true,
		},

		feature: {
			type: 'boolean',
			name: 'Feature enabled',
			defaultStyle: {
				bgcolor: combineRgb(0, 90, 170),
				color: combineRgb(255, 255, 255),
			},
			options: [{ type: 'dropdown', id: 'feature', label: 'Feature', default: 'soundEnabled', choices: FEATURES }],
			callback: ({ options }) => self.settings?.[options.feature] === true,
		},

		disconnected: {
			type: 'boolean',
			name: 'Not connected to PPTimer',
			defaultStyle: {
				bgcolor: combineRgb(80, 80, 80),
				color: combineRgb(255, 120, 120),
			},
			options: [],
			callback: () => self.state == null,
		},
	})
}

import { combineRgb } from '@companion-module/base'
import { FEATURES } from './actions.js'

const WHITE = combineRgb(255, 255, 255)
const BLACK = combineRgb(0, 0, 0)
const DARK = combineRgb(30, 30, 30)

function button(text, actions, extra = {}) {
	return {
		type: 'simple',
		name: text.replace(/\\n/g, ' '),
		style: { text, size: 'auto', color: WHITE, bgcolor: DARK, ...extra.style },
		steps: [{ down: actions, up: [] }],
		feedbacks: extra.feedbacks ?? [],
	}
}

const action = (actionId, options = {}) => ({ actionId, options })

export function UpdatePresets(self) {
	const v = (name) => `$(${self.label}:${name})`

	const phaseFeedbacks = [
		{ feedbackId: 'phase', options: { phase: 'warning' }, style: { bgcolor: combineRgb(230, 160, 0), color: BLACK } },
		{ feedbackId: 'phase', options: { phase: 'critical' }, style: { bgcolor: combineRgb(200, 30, 30), color: WHITE } },
		{ feedbackId: 'phase', options: { phase: 'expired' }, style: { bgcolor: combineRgb(140, 0, 0), color: WHITE } },
		{ feedbackId: 'paused', options: {}, style: { color: combineRgb(150, 150, 150) } },
		{
			feedbackId: 'disconnected',
			options: {},
			style: { bgcolor: combineRgb(60, 60, 60), color: combineRgb(255, 120, 120) },
		},
	]

	const presets = {
		display_toggle: button(v('remaining'), [action('toggle')], {
			feedbacks: phaseFeedbacks,
		}),
		start: button('▶ START', [action('start')], {
			feedbacks: [{ feedbackId: 'running', options: {}, style: { bgcolor: combineRgb(0, 150, 0) } }],
		}),
		pause: button('❚❚ PAUSE', [action('pause')], {
			feedbacks: [{ feedbackId: 'paused', options: {}, style: { bgcolor: combineRgb(180, 110, 0), color: WHITE } }],
		}),
		reset: button('RESET\\n' + v('duration'), [action('reset')]),
		restart: button('RESTART', [action('restart')]),
		add_1m: button('+1 min', [action('add', { direction: 'add', time: '1:00' })]),
		sub_1m: button('−1 min', [action('add', { direction: 'subtract', time: '1:00' })]),
		add_10s: button('+10 s', [action('add', { direction: 'add', time: '10' })]),
		sub_10s: button('−10 s', [action('add', { direction: 'subtract', time: '10' })]),
		overlay_toggle: button('OVERLAY', [action('overlay', { mode: 'togglevisible' })], {
			feedbacks: [{ feedbackId: 'overlay_visible', options: {}, style: { bgcolor: combineRgb(0, 90, 170) } }],
		}),
		presenter_status: button('PRESENTER\\nVIEW', [], {
			style: { color: combineRgb(160, 160, 160) },
			feedbacks: [
				{ feedbackId: 'presenter_view', options: {}, style: { bgcolor: combineRgb(0, 120, 60), color: WHITE } },
			],
		}),
	}

	const durations = [5, 10, 15, 20, 30, 45, 60]
	for (const min of durations) {
		presets[`set_${min}`] = button(`SET\\n${min} min`, [action('set', { time: `${min}:00`, start: 'pause' })])
	}

	const shortNames = {
		warnEnabled: 'AMBER',
		criticalEnabled: 'RED',
		blinkAtZero: 'BLINK\\nAT 0',
		countUp: 'COUNT\\nUP',
		showMinus: 'MINUS\\nSIGN',
		soundEnabled: 'SOUND\\nAT 0',
		transparentBackground: 'TRANSP.\\nBG',
		textOutline: 'OUTLINE',
	}
	for (const { id } of FEATURES) {
		presets[`feature_${id}`] = button(shortNames[id], [action('feature', { feature: id, mode: 'toggle' })], {
			style: { color: combineRgb(160, 160, 160) },
			feedbacks: [
				{ feedbackId: 'feature', options: { feature: id }, style: { bgcolor: combineRgb(0, 90, 170), color: WHITE } },
			],
		})
	}
	presets.testsound = button('TEST\\nSOUND', [action('testsound')])

	self.setPresetDefinitions(
		[
			{
				id: 'control',
				name: 'Timer control',
				definitions: ['display_toggle', 'start', 'pause', 'reset', 'restart', 'overlay_toggle', 'presenter_status'],
			},
			{
				id: 'adjust',
				name: 'Adjust time',
				definitions: ['add_1m', 'sub_1m', 'add_10s', 'sub_10s'],
			},
			{
				id: 'features',
				name: 'Features on / off',
				definitions: [...FEATURES.map(({ id }) => `feature_${id}`), 'testsound'],
			},
			{
				id: 'durations',
				name: 'Set duration',
				definitions: durations.map((m) => `set_${m}`),
			},
		],
		presets,
	)
}

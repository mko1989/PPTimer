import { InstanceBase, InstanceStatus, Regex } from '@companion-module/base'
import { UpgradeScripts } from './upgrades.js'
import { UpdateActions } from './actions.js'
import { UpdateFeedbacks } from './feedbacks.js'
import { UpdateVariableDefinitions, variableValuesFromState } from './variables.js'
import { UpdatePresets } from './presets.js'
import { TimerConnection } from './connection.js'

// Companion (module base 2.x) loads the default export as the instance class.
export { UpgradeScripts }

export default class PPTimerInstance extends InstanceBase {
	constructor(internal) {
		super(internal)
		this.state = null
		this.settings = null
		this.connection = null
	}

	async init(config) {
		this.config = config
		this.updateActions()
		this.updateFeedbacks()
		this.updateVariableDefinitions()
		this.updatePresets()
		this.connect()
	}

	async destroy() {
		this.connection?.close()
		this.connection = null
	}

	async configUpdated(config) {
		this.config = config
		this.updatePresets() // the connection label may have changed
		this.connect()
	}

	getConfigFields() {
		return [
			{
				type: 'static-text',
				id: 'info',
				width: 12,
				label: '',
				value:
					'Connects to the PPTimer PowerPoint add-in. Port and token are in %LOCALAPPDATA%\\PPTimer\\config.json on the presentation PC.',
			},
			{
				type: 'textinput',
				id: 'host',
				label: 'PowerPoint PC (IP or hostname)',
				width: 8,
				default: '',
				regex: Regex.HOSTNAME,
			},
			{
				type: 'number',
				id: 'port',
				label: 'Port',
				width: 4,
				default: 9595,
				min: 1,
				max: 65535,
			},
			{
				type: 'textinput',
				id: 'token',
				label: 'API token (leave empty if not set)',
				width: 8,
				default: '',
			},
		]
	}

	connect() {
		this.connection?.close()
		this.connection = null
		this.setState(null)

		const host = (this.config.host || '').trim()
		if (!host) {
			this.updateStatus(InstanceStatus.BadConfig, 'Set the PowerPoint PC address')
			return
		}

		this.updateStatus(InstanceStatus.Connecting)
		this.connection = new TimerConnection({
			host,
			port: this.config.port || 9595,
			token: (this.config.token || '').trim(),
			log: (level, msg) => this.log(level, msg),
			onStatus: (status, message) => {
				if (status === 'ok') {
					this.updateStatus(InstanceStatus.Ok)
				} else {
					this.updateStatus(InstanceStatus.ConnectionFailure, message ?? null)
					this.setState(null)
				}
			},
			onMessage: (msg) => this.handleMessage(msg),
		})
		this.connection.open()
	}

	handleMessage(msg) {
		switch (msg.type) {
			case 'hello':
				this.settings = msg.settings
				this.checkFeedbacks('feature')
				this.log(
					'info',
					`Connected to PPTimer ${msg.version}${msg.lanAccess ? '' : ' (warning: add-in reports localhost-only)'}`,
				)
				break
			case 'settings':
				this.settings = msg.settings
				this.checkFeedbacks('feature')
				break
			case 'state':
				this.setState(msg)
				break
			case 'event':
				if (msg.event === 'zero') this.log('info', 'Countdown reached zero')
				break
			case 'result':
				if (!msg.ok) this.log('warn', `PPTimer rejected '${msg.cmd}': ${msg.error}`)
				break
		}
	}

	setState(state) {
		this.state = state
		if (!state) this.settings = null
		this.setVariableValues(variableValuesFromState(state))
		this.checkAllFeedbacks()
	}

	/** Sends a command to the add-in, e.g. sendCommand('add', { seconds: 60 }). */
	sendCommand(cmd, args = {}) {
		if (!this.connection?.send({ cmd, ...args })) {
			this.log('warn', `Not connected to PPTimer; '${cmd}' dropped`)
		}
	}

	updateActions() {
		UpdateActions(this)
	}

	updateFeedbacks() {
		UpdateFeedbacks(this)
	}

	updateVariableDefinitions() {
		UpdateVariableDefinitions(this)
	}

	updatePresets() {
		UpdatePresets(this)
	}
}

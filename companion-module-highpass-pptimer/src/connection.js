/**
 * WebSocket link to the PPTimer add-in (Node 22's built-in WebSocket, no dependencies).
 * The add-in pushes {type:"state"} on every change plus a 5 s heartbeat; if nothing
 * arrives for 12 s the link is considered dead and re-opened.
 */
export class TimerConnection {
	constructor({ host, port, token, onStatus, onMessage, log }) {
		this.url = `ws://${host}:${port}/ws` + (token ? `?token=${encodeURIComponent(token)}` : '')
		this.onStatus = onStatus
		this.onMessage = onMessage
		this.log = log
		this.ws = null
		this.closed = false
		this.retryTimer = null
		this.watchdog = null
		this.lastMessageAt = 0
	}

	open() {
		if (this.closed) return
		let ws
		try {
			ws = new WebSocket(this.url)
		} catch (e) {
			this.onStatus('error', e.message)
			this.scheduleReconnect()
			return
		}
		this.ws = ws

		// Tears this socket down and schedules a retry, exactly once per socket.
		const fail = (reason) => {
			if (this.ws !== ws) return
			this.ws = null
			clearTimeout(connectTimer)
			clearInterval(this.watchdog)
			try {
				ws.close()
			} catch {
				// already closing
			}
			if (this.closed) return
			this.onStatus('disconnected', reason)
			this.scheduleReconnect()
		}

		// An unreachable host can leave the TCP connect hanging for a minute or more.
		const connectTimer = setTimeout(() => fail('connect timeout'), 5000)

		ws.addEventListener('open', () => {
			if (this.ws !== ws) return
			clearTimeout(connectTimer)
			this.lastMessageAt = Date.now()
			this.onStatus('ok')
			clearInterval(this.watchdog)
			this.watchdog = setInterval(() => {
				if (Date.now() - this.lastMessageAt > 12000) {
					this.log('warn', 'No data from PPTimer for 12 s, reconnecting')
					fail('heartbeat timeout')
				}
			}, 3000)
		})

		ws.addEventListener('message', (event) => {
			if (this.ws !== ws) return
			this.lastMessageAt = Date.now()
			let msg
			try {
				msg = JSON.parse(event.data)
			} catch {
				return
			}
			this.onMessage(msg)
		})

		ws.addEventListener('close', (event) => fail(event.reason || `connection closed (${event.code})`))
		// Node's WebSocket fires only 'error' (no 'close') when the connection is refused.
		ws.addEventListener('error', (event) => fail(event.message || 'connection error'))
	}

	scheduleReconnect() {
		clearTimeout(this.retryTimer)
		if (this.closed) return
		this.retryTimer = setTimeout(() => this.open(), 2000)
	}

	/** @returns {boolean} whether the message was sent */
	send(message) {
		if (this.ws?.readyState !== WebSocket.OPEN) return false
		this.ws.send(JSON.stringify(message))
		return true
	}

	close() {
		this.closed = true
		clearTimeout(this.retryTimer)
		clearInterval(this.watchdog)
		const ws = this.ws
		this.ws = null
		try {
			ws?.close()
		} catch {
			// already closing
		}
	}
}

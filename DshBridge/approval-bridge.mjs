import net from 'node:net'

export const name = 'codex-pond-approval-bridge'

function exchange(pipeName, request, timeoutMs = 600_000) {
  return new Promise((resolve) => {
    const address = process.platform === 'win32' ? `\\\\.\\pipe\\${pipeName}` : pipeName
    const client = net.createConnection(address)
    let buffer = ''
    let settled = false
    const finish = (decision) => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      client.destroy()
      resolve(decision)
    }
    const timer = setTimeout(() => finish('unavailable'), timeoutMs)
    client.on('connect', () => client.write(`${JSON.stringify(request)}\n`))
    client.on('data', (chunk) => {
      buffer += chunk.toString('utf8')
      const end = buffer.indexOf('\n')
      if (end < 0) return
      try {
        const response = JSON.parse(buffer.slice(0, end))
        finish(response?.decision === 'allow' ? 'allowed-once' : 'rejected')
      } catch {
        finish('unavailable')
      }
    })
    client.on('error', () => finish('unavailable'))
    client.on('close', () => finish('unavailable'))
  })
}

function announceReady(pipeName) {
  return new Promise((resolve) => {
    const address = process.platform === 'win32' ? `\\\\.\\pipe\\${pipeName}` : pipeName
    const client = net.createConnection(address)
    let buffer = ''
    let settled = false
    const finish = (ready) => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      client.destroy()
      resolve(ready)
    }
    const timer = setTimeout(() => finish(false), 10_000)
    client.on('connect', () => client.write('{"type":"bridge_ready","protocolVersion":1}\n'))
    client.on('data', (chunk) => {
      buffer += chunk.toString('utf8')
      const end = buffer.indexOf('\n')
      if (end < 0) return
      try {
        const response = JSON.parse(buffer.slice(0, end))
        finish(response?.ready === true && response?.protocolVersion === 1)
      } catch {
        finish(false)
      }
    })
    client.on('error', () => finish(false))
    client.on('close', () => finish(false))
  })
}

export function apply(ctx) {
  const pipeName = process.env.CODEX_POND_APPROVAL_PIPE
  const readyPipeName = process.env.CODEX_POND_APPROVAL_READY_PIPE
  ctx.root.on('approval/request', async (request, next) => {
    if (!pipeName) return 'unavailable'
    const decision = await exchange(pipeName, {
      toolName: request?.toolName ?? '受保护工具',
      reason: request?.displayReason ?? request?.reason ?? 'DeepSeek Harness 请求一次性操作授权。',
      callId: request?.callId ?? null,
    })
    if (decision === 'allowed-once' || decision === 'rejected') return decision
    return 'unavailable'
  })
  if (!pipeName || !readyPipeName) throw new Error('Codex Pond approval bridge pipes are required in headless mode.')
  void announceReady(readyPipeName).then((ready) => {
    if (!ready) process.exitCode = 72
  })
}

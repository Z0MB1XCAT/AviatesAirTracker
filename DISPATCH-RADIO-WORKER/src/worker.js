export { DispatchRadioRoom } from './room.js';

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === '/health') {
      return new Response('ok', { status: 200 });
    }

    // Any new route needing the admin key must be added to this `if` block directly —
    // the `else if` below only distinguishes "known path" from 404, it does not gate.
    if (url.pathname === '/admin') {
      if (!env.ADMIN_WS_KEY) {
        return new Response('Admin key not configured', { status: 500 });
      }
      const key = url.searchParams.get('key');
      if (key !== env.ADMIN_WS_KEY) {
        return new Response('Unauthorized', { status: 401 });
      }
    } else if (url.pathname === '/voice') {
      // A dispatcher voice link carries `key` (same admin gating as
      // /admin); a pilot voice link carries `pilotId` instead and needs
      // no key, matching /pilot's existing no-auth behavior.
      const key = url.searchParams.get('key');
      if (key !== null) {
        if (!env.ADMIN_WS_KEY) {
          return new Response('Admin key not configured', { status: 500 });
        }
        if (key !== env.ADMIN_WS_KEY) {
          return new Response('Unauthorized', { status: 401 });
        }
      } else if (!url.searchParams.get('pilotId')) {
        return new Response('pilotId required', { status: 400 });
      }
    } else if (url.pathname !== '/pilot') {
      return new Response('Not found', { status: 404 });
    }

    const id = env.DISPATCH_ROOM.idFromName('global');
    const stub = env.DISPATCH_ROOM.get(id);
    return stub.fetch(request);
  },
};

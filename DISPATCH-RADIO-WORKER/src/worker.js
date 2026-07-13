export { DispatchRadioRoom } from './room.js';

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === '/health') {
      return new Response('ok', { status: 200 });
    }

    if (url.pathname === '/admin') {
      const key = url.searchParams.get('key');
      if (env.ADMIN_WS_KEY && key !== env.ADMIN_WS_KEY) {
        return new Response('Unauthorized', { status: 401 });
      }
    } else if (url.pathname !== '/pilot') {
      return new Response('Not found', { status: 404 });
    }

    const id = env.DISPATCH_ROOM.idFromName('global');
    const stub = env.DISPATCH_ROOM.get(id);
    return stub.fetch(request);
  },
};

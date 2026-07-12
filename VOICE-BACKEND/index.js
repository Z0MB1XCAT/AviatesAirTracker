import { createServer } from './src/server.js';
import { config } from './src/config.js';

const { httpServer } = createServer(config);
httpServer.listen(config.port, () => {
  console.log(`Dispatch backend listening on :${config.port}`);
});

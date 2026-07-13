export const config = {
  port: Number(process.env.PORT || 8080),
  dispatchLat: 52.8311,
  dispatchLon: -1.3281,
  clearRangeNm: 200,
  degradedRangeNm: 400,
  staleSessionMs: 15000,
  sweepIntervalMs: 5000,
  adminKey: process.env.ADMIN_WS_KEY || '',
};

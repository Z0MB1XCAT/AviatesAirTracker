/* ═══════════════════════════════════════════════════════════
   AviatesAir EFB — client (iOS springboard)
   Vanilla JS, no build step. Talks to the desktop host over a
   token-guarded REST API + WebSocket.
   ═══════════════════════════════════════════════════════════ */
(() => {
'use strict';
const $  = (s,r=document)=>r.querySelector(s);
const $$ = (s,r=document)=>[...r.querySelectorAll(s)];
const clamp=(v,a,b)=>Math.max(a,Math.min(b,v));
const num=(v,d=0)=>(v==null||isNaN(v))?'—':Number(v).toFixed(d);
const esc=t=>{const d=document.createElement('div');d.textContent=t==null?'':t;return d.innerHTML;};

// ── token / auth ──────────────────────────────────────────
let token='';
(function(){ const m=location.hash.match(/t=([a-f0-9]+)/i);
  if(m){token=m[1];localStorage.setItem('efbToken',token);history.replaceState(null,'',location.pathname);}
  else token=localStorage.getItem('efbToken')||''; })();
async function api(p,o={}){ o.headers=Object.assign({'X-EFB-Token':token,'Content-Type':'application/json'},o.headers||{});
  const r=await fetch(p,o); if(r.status===401){showPairing();throw new Error('unauthorized');} return r; }
const getJSON =async p=>(await api(p)).json();
const postJSON=async(p,b)=>(await api(p,{method:'POST',body:JSON.stringify(b||{})})).json();

// ── state ─────────────────────────────────────────────────
let state={}, plan={hasPlan:false}, checklists=[], msg={friends:[],messages:[],unreadCount:0}, aircraft=[];
let activeApp=null, appCleanup=null;

async function boot(){
  try{ const h=await getJSON('/efb/api/hello'); if(!h.tokenValid){showPairing();return;} }
  catch{ showPairing(); return; }
  $('#pairing').classList.add('hidden'); $('#app').classList.remove('hidden');
  loadPrefs(); buildSpringboard(); loadWallpaper();
  try{ aircraft=await getJSON('/efb/api/aircraft'); }catch{}
  try{ state=await getJSON('/efb/api/state'); }catch{}
  try{ plan =await getJSON('/efb/api/flightplan'); }catch{}
  try{ checklists=await getJSON('/efb/api/checklists'); }catch{}
  try{ msg=await getJSON('/efb/api/messages'); }catch{}
  renderLive(); renderBadges();
  connectWS(); startClock();
  $('#appBack').onclick=closeApp; $('#homeInd').onclick=closeApp;
  $('#flightWidget').onclick=()=>openApp('flight');
}

// ── pairing ───────────────────────────────────────────────
function showPairing(){
  $('#app').classList.add('hidden'); const p=$('#pairing'); p.classList.remove('hidden');
  const b=$$('#codeInput input'); b[0].focus();
  b.forEach((x,i)=>{ x.oninput=()=>{x.value=x.value.replace(/\D/g,'');if(x.value&&i<5)b[i+1].focus();};
    x.onkeydown=e=>{if(e.key==='Backspace'&&!x.value&&i>0)b[i-1].focus();}; });
  $('#pairBtn').onclick=doPair;
}
async function doPair(){
  const code=$$('#codeInput input').map(x=>x.value).join('');
  if(code.length!==6){$('#pairErr').textContent='Enter all 6 digits';return;}
  try{ const r=await fetch('/efb/api/pair',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({code})});
    if(!r.ok){$('#pairErr').textContent='Incorrect code — check the desktop app';return;}
    const d=await r.json(); token=d.token; localStorage.setItem('efbToken',token); boot();
  }catch{ $('#pairErr').textContent='Connection failed'; }
}

// ── websocket ─────────────────────────────────────────────
let ws,wsRetry=0,pollTimer=null;
function connectWS(){
  const proto=location.protocol==='https:'?'wss':'ws';
  try{ ws=new WebSocket(`${proto}://${location.host}/efb/ws?t=${token}`);}catch{startPolling();return;}
  ws.onopen=()=>{wsRetry=0;stopPolling();setConn(true);};
  ws.onmessage=ev=>{ let m; try{m=JSON.parse(ev.data);}catch{return;}
    if(m.type==='snapshot'){state=m.data;renderLive();}
    else if(m.type==='flightplan'){plan=m.data;renderLive();if(activeApp==='ofp')renderApp();}
    else if(m.type==='checklists'){checklists=m.data;if(activeApp==='checklist')renderApp();}
    else if(m.type==='messages'){msg=m.data;renderBadges();if(activeApp==='messages')renderApp();}
  };
  ws.onclose=()=>{setConn(false);startPolling();wsRetry=Math.min(wsRetry+1,6);setTimeout(connectWS,800*wsRetry);};
  ws.onerror=()=>{try{ws.close();}catch{}};
}
function startPolling(){ if(pollTimer)return; pollTimer=setInterval(async()=>{try{state=await getJSON('/efb/api/state');renderLive();}catch{}},1500); }
function stopPolling(){ if(pollTimer){clearInterval(pollTimer);pollTimer=null;} }
function setConn(live){ $('#connDot').classList.toggle('live',live&&state.simConnected!==false); }

function startClock(){ const t=()=>{const d=new Date();$('#sbTime').textContent=String(d.getHours()).padStart(2,'0')+':'+String(d.getMinutes()).padStart(2,'0');}; t(); setInterval(t,10000); }

// ── wallpaper ─────────────────────────────────────────────
async function loadWallpaper(){
  // 1. pilot-uploaded wallpaper (stored on the desktop host, shared across devices)
  try{
    const r=await fetch('/efb/api/wallpaper',{headers:{'X-EFB-Token':token}});
    if(r.status===200){ const blob=await r.blob(); const url=URL.createObjectURL(blob);
      setWallpaperImg(url); return; }
  }catch{}
  // 2. bundled default shipped in EfbWeb/ (wallpaper.jpg | .png), if present
  for(const f of ['wallpaper.jpg','wallpaper.png']){
    try{ const r=await fetch('/'+f,{method:'HEAD'}); if(r.ok){ setWallpaperImg('/'+f); return; } }catch{}
  }
  // 3. built-in gradient
  document.documentElement.removeAttribute('data-wallpaper');
}
function setWallpaperImg(url){
  document.documentElement.style.setProperty('--wallpaper-img',`url('${url}')`);
  document.documentElement.dataset.wallpaper='custom';
}

// ═══ SPRINGBOARD ═══════════════════════════════════════════
const APPS=[
  {id:'flight',   name:'Flight',         glyph:'✈', tile:'t-flight'},
  {id:'depperf',  name:'Departure Perf', glyph:'↑', tile:'t-dep'},
  {id:'arrperf',  name:'Arrival Perf',   glyph:'↓', tile:'t-arr'},
  {id:'atis',     name:'ATIS',           glyph:'☰', tile:'t-atis'},
  {id:'map',      name:'Map',            glyph:'❖', tile:'t-map'},
  {id:'messages', name:'Messages',       glyph:'✉', tile:'t-msg'},
  {id:'acars',    name:'ACARS',          glyph:'⇅', tile:'t-acars'},
  {id:'ofp',      name:'OFP',            glyph:'▤', tile:'t-ofp'},
  {id:'checklist',name:'Checklist',      glyph:'☑', tile:'t-chk'},
  {id:'landing',  name:'Landing',        glyph:'◎', tile:'t-land'},
  {id:'scratch',  name:'Scratchpad',     glyph:'✎', tile:'t-scr'},
  {id:'pa',       name:'Cabin PA',       glyph:'☎', tile:'t-pa'},
  {id:'manuals',  name:'Manuals',        glyph:'❏', tile:'t-man'},
  {id:'game',     name:'Approach!',      glyph:'🕹', tile:'t-game'},
  {id:'settings', name:'Settings',       glyph:'⚙', tile:'t-set'},
];
const DOCK=['flight','map','messages','settings'];
function iconHtml(a){ return `<button class="icon" data-app="${a.id}">
  <div class="tile ${a.tile}">${a.glyph}<span class="badge hidden" data-badge="${a.id}"></span></div>
  <span class="lbl">${a.name}</span></button>`; }
function buildSpringboard(){
  $('#appGrid').innerHTML=APPS.map(iconHtml).join('');
  $('#dock').innerHTML=DOCK.map(id=>iconHtml(APPS.find(a=>a.id===id))).join('');
  $$('#app .icon').forEach(el=>el.onclick=()=>openApp(el.dataset.app));
}
function renderBadges(){
  const n=msg.unreadCount||0;
  $$('[data-badge="messages"]').forEach(b=>{ b.textContent=n>99?'99+':n; b.classList.toggle('hidden',n<=0); });
}

// ═══ APP HOST ══════════════════════════════════════════════
const APP_META={ flight:'Flight', depperf:'Departure Performance', arrperf:'Arrival Performance',
  atis:'ATIS', map:'Map', messages:'Messages', acars:'ACARS Log', ofp:'SimBrief OFP',
  checklist:'Checklists', landing:'Landing Report', scratch:'Scratchpad', pa:'Cabin PA',
  game:'Approach!', manuals:'Manuals', settings:'Settings' };
function openApp(id){
  if(appCleanup){appCleanup();appCleanup=null;}
  activeApp=id;
  $('#appName').textContent=APP_META[id]||id;
  $('#appActions').innerHTML='';
  const host=$('#appHost'); host.classList.remove('hidden','closing');
  renderApp();
}
function closeApp(){
  if(!activeApp)return;
  const host=$('#appHost'); host.classList.add('closing');
  setTimeout(()=>{ host.classList.add('hidden'); if(appCleanup){appCleanup();appCleanup=null;} activeApp=null; },240);
}
function renderApp(){
  const body=$('#appBody'); body.className='app-body'; body.innerHTML='';
  ({flight:renderFlight, depperf:()=>renderPerf('takeoff'), arrperf:()=>renderPerf('landing'),
    atis:renderAtis, map:renderMap, messages:renderMessages, acars:renderAcars, ofp:renderOfp,
    checklist:renderChecklist, landing:renderLanding, scratch:renderScratch, pa:renderPa,
    game:renderGame, manuals:renderManuals, settings:renderSettings}[activeApp]||(()=>{}))(body);
}

// ═══ LIVE (widget + statusbar + flight app) ═══════════════
function renderLive(){
  const s=state||{};
  setConn(ws&&ws.readyState===1);
  document.documentElement.style.setProperty('--phase',s.phaseColor||'var(--accent)');
  // widget
  $('#wgCs').textContent=s.callsign||s.flightNumber||'AVIATESAIR';
  const wp=$('#wgPhase'); wp.textContent=(s.phase||'STANDBY').toUpperCase(); wp.style.setProperty('--phase',s.phaseColor||'var(--accent)');
  $('#wgGlow').style.setProperty('--phase',s.phaseColor?`color-mix(in srgb,${s.phaseColor} 35%,transparent)`:'var(--accent-glow)');
  $('#wgDep').textContent=s.departureIcao||'----'; $('#wgArr').textContent=s.arrivalIcao||'----';
  $('#wgPlane').style.left=clamp(s.progressPct||0,0,100)+'%';
  const fl=s.altitudeFt>=18000?'FL'+Math.round(s.altitudeFt/100):(s.altitudeFt?num(s.altitudeFt)+' ft':'On ground');
  $('#wgFl').textContent=s.hasActiveFlight?fl:'On ground';
  $('#wgEta').textContent=s.hasActiveFlight?(s.eteMinutes>0?'Landing in '+etaText(s):(s.distanceRemainingNm?num(s.distanceRemainingNm)+' nm':'—')):'—';
  if(activeApp==='flight')updateFlight();
}
function etaText(s){ if(s.eteMinutes>0){const h=Math.floor(s.eteMinutes/60),m=s.eteMinutes%60;return (h?h+'h ':'')+m+'m';} return '—'; }

// ── FLIGHT APP ──
const INSTR=[
  {id:'alt',l:'Altitude',u:'ft',a:1,g:s=>num(s.altitudeFt)},
  {id:'ias',l:'IAS',u:'kt',g:s=>num(s.iasKts)},
  {id:'gs',l:'GS',u:'kt',g:s=>num(s.groundKts)},
  {id:'mach',l:'Mach',u:'',g:s=>s.mach?num(s.mach,3):'—'},
  {id:'hdg',l:'HDG',u:'°',g:s=>num(s.headingDeg)},
  {id:'vs',l:'V/S',u:'fpm',g:s=>(s.verticalSpeedFpm>0?'+':'')+num(s.verticalSpeedFpm)},
  {id:'oat',l:'OAT',u:'°C',g:s=>num(s.oatC)},
  {id:'qnh',l:'QNH',u:'mb',g:s=>num(s.qnhMb)},
];
function renderFlight(body){
  body.innerHTML=`
    <div class="inst-grid">${INSTR.map(i=>`<div class="inst ${i.a?'accent':''}"><div class="inst-label">${i.l}</div>
      <div class="inst-val" id="iv-${i.id}">—<small>${i.u}</small></div></div>`).join('')}</div>
    <div class="panel advisor" id="advisor"></div>
    <div class="panel"><div class="panel-h"><span>Fuel &amp; Configuration</span><span class="panel-tag" id="fpTag"></span></div>
      <div class="fuel-bar"><div class="fuel-fill" id="fuelFill"></div></div>
      <div class="cfg-rows" id="cfgRows"></div></div>`;
  updateFlight();
}
function updateFlight(){
  const s=state||{};
  INSTR.forEach(i=>{const el=$('#iv-'+i.id);if(el)el.innerHTML=`${i.g(s)}<small>${i.u}</small>`;});
  const fill=$('#fuelFill'); if(fill)fill.style.width=clamp((s.fuelPct||0)*100,0,100)+'%';
  const tag=$('#fpTag'); if(tag)tag.textContent=plan.hasPlan?`${plan.departureIcao}–${plan.arrivalIcao}`:'';
  const adv=$('#advisor');
  if(adv){
    if(!s.hasActiveFlight){
      adv.innerHTML=`<div class="panel-h"><span>Descent &amp; Fuel Advisor</span></div>
        <div class="adv-idle">Available once airborne with a flight plan loaded.</div>`;
    }else{
      const fs=s.fuelStatus||'OK';
      const fuelCol=fs==='LOW'?'var(--bad)':fs==='CAUTION'?'var(--caution)':'var(--good)';
      const todMain=s.descendNow?'DESCEND NOW'
        :(s.todDistanceNm>0?`${num(s.todDistanceNm)}<small> nm</small>`:'—');
      const todSub=s.descendNow?'At or past computed descent point'
        :(s.todMinutes>0?`in ${s.todMinutes} min`:(s.todDistanceNm>0?'to top of descent':'—'));
      adv.innerHTML=`<div class="panel-h"><span>Descent &amp; Fuel Advisor</span>
          <span class="panel-tag">3:1 + 10 nm</span></div>
        <div class="adv-grid">
          <div class="adv-cell ${s.descendNow?'hot':''}">
            <div class="adv-k">Top of Descent</div>
            <div class="adv-v">${todMain}</div>
            <div class="adv-s">${todSub}</div></div>
          <div class="adv-cell">
            <div class="adv-k">Required Gradient</div>
            <div class="adv-v">${s.requiredDescentFtPerNm>0?num(s.requiredDescentFtPerNm)+'<small> ft/nm</small>':'—'}</div>
            <div class="adv-s">to field elevation</div></div>
          <div class="adv-cell">
            <div class="adv-k">Fuel on Arrival</div>
            <div class="adv-v" style="color:${fuelCol}">${s.predictedLandingFuelLbs>0?num(s.predictedLandingFuelLbs)+'<small> lb</small>':'—'}</div>
            <div class="adv-s">${s.plannedLandingFuelLbs>0?'plan '+num(s.plannedLandingFuelLbs)+' lb':'no OFP figure'}</div></div>
          <div class="adv-cell">
            <div class="adv-k">Fuel Status</div>
            <div class="adv-v" style="color:${fuelCol};font-size:22px">${fs}</div>
            <div class="adv-s">${fs==='OK'?'at or above plan':'below planned arrival fuel'}</div></div>
        </div>`;
    }
  }
  const rows=$('#cfgRows'); if(rows)rows.innerHTML=[
    ['Fuel',num(s.fuelLbs)+' lb'],['Burn',num(s.fuelBurnPph)+' pph'],
    ['Gear',s.gear||'—',s.gear==='DN'?'off':'on'],['Flaps',num(s.flapsPct)+'%'],
    ['Autopilot',s.apModes||'OFF',s.apMaster?'on':'off'],['Squawk',s.squawk||'2000'],
  ].map(([k,v,c])=>`<div class="cfg-row"><span class="k">${k}</span><span class="v ${c||''}">${v}</span></div>`).join('');
}

// ── PERFORMANCE APPS ──
function acOptions(sel){ return aircraft.map(a=>`<option value="${a.code}" ${a.code===sel?'selected':''}>${a.name}</option>`).join(''); }
function flapOptions(code,kind){ const a=aircraft.find(x=>x.code===code)||aircraft[0]; const l=a?(kind==='takeoff'?a.takeoffFlaps:a.landingFlaps):[]; return (l||[]).map(f=>`<option>${f}</option>`).join(''); }
function pickAircraft(t){ if(!aircraft.length)return''; const h=aircraft.find(a=>t&&(t.toUpperCase().includes(a.code)||a.code===t)); return (h||aircraft[0]).code; }
function rwyHeading(r){ if(!r)return''; const m=String(r).match(/(\d{1,2})/); return m?parseInt(m[1])*10:''; }
// Minimal METAR decode for autofilling performance (wind / temp / QNH).
function parseMetar(m){
  const out={};
  if(!m) return out;
  const p=m.split(/\s+/);
  const w=p.find(x=>/^(\d{3}|VRB)\d{2}(G\d{2})?KT$/.test(x));
  if(w){ const d=w.slice(0,3); out.windDir=d==='VRB'?0:parseInt(d,10); out.windKts=parseInt(w.slice(3,5),10); }
  const t=p.find(x=>/^M?\d{2}\/M?\d{2}$/.test(x));
  if(t){ const tt=t.split('/')[0]; out.tempC=parseInt(tt.replace('M','-'),10); }
  const q=p.find(x=>/^Q\d{4}$/.test(x));
  if(q) out.qnh=parseInt(q.slice(1),10);
  return out;
}
function renderPerf(mode,body){
  body=body||$('#appBody');
  const s=state||{}, def=pickAircraft(s.aircraftType);
  const dep = mode==='takeoff';
  // Landing weight ≈ zero-fuel weight + planned landing fuel
  const wt=dep?(plan.takeoffWeightKg?Math.round(plan.takeoffWeightKg*2.20462):'')
    :(plan.zeroFuelWeightKg?Math.round((plan.zeroFuelWeightKg*2.20462)+(plan.landingFuelLbs||0)):'');
  const rh=dep?rwyHeading(plan.departureRunway):rwyHeading(plan.arrivalRunway);
  // Field data straight from the SimBrief OFP
  const elev=(dep?plan.departureElevationFt:plan.arrivalElevationFt)||'';
  const rwyLen=(dep?plan.departureRunwayLengthM:plan.arrivalRunwayLengthM)||'';
  // Weather: live sim values win; otherwise decode the OFP METAR for that field
  const mx=parseMetar(dep?plan.departureMetar:plan.arrivalMetar);
  const liveOk = s.simConnected && s.hasActiveFlight;
  const wdir = liveOk&&num(s.windDirDeg)!=='—'?Math.round(s.windDirDeg):(mx.windDir??'');
  const wspd = liveOk&&num(s.windKts)!=='—'?Math.round(s.windKts):(mx.windKts??'');
  const oat  = liveOk&&num(s.oatC)!=='—'?Math.round(s.oatC):(mx.tempC??15);
  const qnh  = liveOk&&num(s.qnhMb)!=='—'?Math.round(s.qnhMb):(mx.qnh??1013);
  body.innerHTML=`
    ${plan.hasPlan?`<div class="autofill">Autofilled from SimBrief · ${dep?plan.departureIcao:plan.arrivalIcao}
      ${(dep?plan.departureRunway:plan.arrivalRunway)?' RWY '+(dep?plan.departureRunway:plan.arrivalRunway):''}
      ${liveOk?'· live wind':(mx.windDir!=null?'· METAR wind':'')}</div>`
      :`<div class="autofill dim">No SimBrief plan loaded — enter values manually.</div>`}
    <form class="perf-form" id="pf">
      <div class="fld wide"><label>Aircraft</label><select name="ac">${acOptions(def)}</select></div>
      <div class="fld"><label>Weight (lb)</label><input name="weight" inputmode="numeric" value="${wt||''}" placeholder="145000"></div>
      <div class="fld"><label>Flaps</label><select name="flap">${flapOptions(def,mode)}</select></div>
      <div class="fld"><label>Runway length (m)</label><input name="rwy" inputmode="numeric" value="${rwyLen}" placeholder="3000"></div>
      <div class="fld"><label>Elevation (ft)</label><input name="elev" inputmode="numeric" value="${elev}" placeholder="80"></div>
      <div class="fld"><label>Runway hdg (°)</label><input name="rhdg" inputmode="numeric" value="${rh||''}" placeholder="270"></div>
      <div class="fld"><label>Wind dir (°)</label><input name="wdir" inputmode="numeric" value="${wdir}"></div>
      <div class="fld"><label>Wind (kt)</label><input name="wspd" inputmode="numeric" value="${wspd}"></div>
      <div class="fld"><label>OAT (°C)</label><input name="oat" inputmode="numeric" value="${oat}"></div>
      <div class="fld"><label>QNH (mb)</label><input name="qnh" inputmode="numeric" value="${qnh}"></div>
      <div class="fld"><label>Slope (%)</label><input name="slope" inputmode="decimal" value="0"></div>
      ${mode==='landing'?`<div class="fld"><label>Autobrake</label><select name="ab"><option>LOW</option><option selected>MED</option><option>MAX</option></select></div>`:''}
      <div class="fld check"><input type="checkbox" name="wet" id="wet"><label for="wet" style="text-transform:none;letter-spacing:0">Wet runway</label></div>
      <button class="btn-primary" type="button" id="calc">Calculate ${mode==='takeoff'?'Takeoff':'Landing'}</button>
    </form>
    <div id="pout"></div>`;
  const f=$('#pf');
  f.querySelector('[name=ac]').onchange=e=>{f.querySelector('[name=flap]').innerHTML=flapOptions(e.target.value,mode);};
  $('#calc').onclick=async()=>{
    const g=n=>f.querySelector(`[name=${n}]`), v=n=>parseFloat(g(n)?.value)||0;
    const req={aircraftCode:g('ac').value,weightLbs:v('weight'),flapSetting:g('flap').value,runwayLengthM:v('rwy'),
      runwayElevationFt:v('elev'),runwayHeadingDeg:v('rhdg'),windDirDeg:v('wdir'),windKts:v('wspd'),
      oatC:v('oat'),qnhMb:v('qnh')||1013.25,runwaySlopePct:v('slope'),wetRunway:g('wet').checked};
    if(mode==='landing')req.autobrake=g('ab').value;
    try{ renderPerfResult(mode,await postJSON('/efb/api/perf/'+mode,req)); }
    catch{ $('#pout').innerHTML=`<div class="empty">Calculation failed.</div>`; }
  };
}
function renderPerfResult(mode,r){
  const speeds=mode==='takeoff'?[['V1',r.v1,'kt'],['VR',r.vr,'kt'],['V2',r.v2,'kt']]:[['VREF',r.vref,'kt'],['VAPP',r.vapp,'kt'],['FLAP',r.flapSetting,'']];
  const dist=mode==='takeoff'?[['Takeoff distance',r.takeoffDistanceM+' m'],['Required runway',r.requiredRunwayM+' m']]:[['Landing distance (factored)',r.landingDistanceM+' m'],['Autobrake',r.autobrake]];
  const usedM=mode==='takeoff'?r.requiredRunwayM:r.landingDistanceM;
  const mp=r.runwayLengthM?clamp(100*(1-usedM/r.runwayLengthM),0,100):0;
  const mc=r.marginM<0?'var(--bad)':r.marginM<300?'var(--caution)':'var(--good)';
  $('#pout').innerHTML=`<div class="panel">
    <div class="panel-h"><span>${r.aircraftName}</span><span class="panel-tag">${r.flapSetting}</span></div>
    <div class="vrow">${speeds.map(([k,val,u])=>`<div class="vspd"><div class="k">${k}</div><div class="n">${val}</div><div class="u">${u}</div></div>`).join('')}</div>
    <div class="perf-status ${r.status}">${r.status==='OK'?'✓ Within limits':r.status==='CAUTION'?'▲ Review cautions':'✕ Not within limits'}</div>
    ${dist.map(([k,v])=>`<div class="dist-row"><span class="k">${k}</span><span>${v}</span></div>`).join('')}
    ${r.runwayLengthM?`<div class="margin-bar"><div class="margin-fill" style="width:${mp}%;background:${mc}"></div></div>
      <div class="dist-row"><span class="k">Runway margin</span><span style="color:${mc}">${r.marginM>=0?'+':''}${r.marginM} m</span></div>`:''}
    <div class="dist-row"><span class="k">Head / Cross wind</span><span>${r.headwindKts>=0?'H':'T'}${Math.abs(r.headwindKts)} / X${Math.abs(r.crosswindKts)} kt</span></div>
    <div class="dist-row"><span class="k">Density altitude</span><span>${r.densityAltitudeFt} ft</span></div>
    ${r.warnings&&r.warnings.length?`<ul class="warn-list">${r.warnings.map(w=>`<li>${esc(w)}</li>`).join('')}</ul>`:''}
    <p class="set-note">Training-grade estimate — not certified AFM/QRH data.</p></div>`;
}

// ── CHECKLIST APP ──
function renderChecklist(body){
  $('#appActions').innerHTML=`<button class="btn-ghost" id="clReset">Reset all</button>`;
  $('#clReset').onclick=()=>postJSON('/efb/api/checklists/reset',{}).catch(()=>{});
  body.innerHTML=checklists.map(c=>{
    const pct=c.totalCount?Math.round(100*c.checkedCount/c.totalCount):0, done=c.isComplete;
    return `<div class="cl-card" data-cl="${c.id}"><div class="cl-card-h">
      <div class="ring ${done?'done':''}" style="--p:${pct}"><span>${done?'✓':c.checkedCount+'/'+c.totalCount}</span></div>
      <div class="cl-title">${c.name}<small>${c.phase}</small></div></div>
      <div class="cl-items">${c.items.map(it=>`<div class="cl-item ${it.isChecked?'checked':''}" data-item="${it.id}">
        <div class="cl-box">✓</div><div class="cl-ch">${it.challenge}</div><div class="cl-rsp">${it.response}</div>
        <div class="cl-ts">${it.checkedAtUtc?fmtT(it.checkedAtUtc):''}</div></div>`).join('')}</div></div>`;
  }).join('');
  $$('#appBody .cl-item').forEach(row=>row.onclick=()=>{
    const cl=row.closest('.cl-card').dataset.cl,id=row.dataset.item,on=!row.classList.contains('checked');
    row.classList.toggle('checked',on);
    postJSON('/efb/api/checklists/item',{checklistId:cl,itemId:id,checked:on}).catch(()=>{});
  });
}
function fmtT(iso){ try{return new Date(iso).toISOString().substr(11,5)+'Z';}catch{return '';} }

// ── MANUALS APP ──
let docCat='All',docTimer=null;
function renderManuals(body){
  body.innerHTML=`<input class="search" id="dq" placeholder="Search manuals, SOPs, procedures…">
    <div class="doc-cats" id="dcats"></div><div id="dlist"></div>`;
  $('#dq').oninput=()=>{clearTimeout(docTimer);docTimer=setTimeout(loadDocs,220);};
  loadDocs();
}
async function loadDocs(){
  const q=$('#dq')?$('#dq').value.trim():'';
  let d; try{ d=await getJSON(`/efb/api/manuals?category=${encodeURIComponent(docCat)}&q=${encodeURIComponent(q)}`);}catch{return;}
  const cats=['All',...d.categories];
  $('#dcats').innerHTML=cats.map(c=>`<span class="cat ${c===docCat?'active':''}" data-c="${c}">${c}</span>`).join('');
  $$('#dcats .cat').forEach(c=>c.onclick=()=>{docCat=c.dataset.c;loadDocs();});
  $('#dlist').innerHTML=d.documents.map(doc=>`<div class="doc-card" data-id="${doc.id}">
    <span class="doc-bm ${doc.bookmarked?'on':''}" data-bm="${doc.id}">${doc.bookmarked?'★':'☆'}</span>
    <div class="doc-cat-tag">${doc.category}</div><h4>${doc.title}</h4><p>${doc.summary}</p></div>`).join('')||`<div class="empty">No documents match “${esc(q)}”.</div>`;
  $$('#dlist .doc-card').forEach(card=>card.onclick=e=>{
    if(e.target.dataset.bm){postJSON(`/efb/api/manuals/${e.target.dataset.bm}/bookmark`,{}).then(loadDocs).catch(()=>{});return;}
    openDoc(card.dataset.id);
  });
}
async function openDoc(id){
  let doc; try{doc=await getJSON('/efb/api/manuals/'+id);}catch{return;}
  $('#appActions').innerHTML=`<button class="btn-ghost" id="docBack">‹ Library</button>`;
  $('#docBack').onclick=()=>{$('#appActions').innerHTML='';renderManuals($('#appBody'));};
  $('#appBody').innerHTML=`<article class="doc-body">${md(doc.bodyMarkdown)}</article>`;
}
function md(src){
  const e=t=>t.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
  const inl=t=>e(t).replace(/\*\*([^*]+)\*\*/g,'<strong>$1</strong>').replace(/`([^`]+)`/g,'<code>$1</code>');
  const L=src.split('\n');let h='',i=0;
  while(i<L.length){let l=L[i];
    if(/^\|(.+)\|/.test(l)&&/^\|[\s:-]+\|/.test(L[i+1]||'')){const rows=[];while(i<L.length&&/^\|/.test(L[i])){rows.push(L[i]);i++;}
      const cc=r=>r.split('|').slice(1,-1).map(c=>c.trim());const hd=cc(rows[0]),bd=rows.slice(2).map(cc);
      h+=`<table><thead><tr>${hd.map(x=>`<th>${inl(x)}</th>`).join('')}</tr></thead><tbody>${bd.map(r=>`<tr>${r.map(c=>`<td>${inl(c)}</td>`).join('')}</tr>`).join('')}</tbody></table>`;continue;}
    if(/^# /.test(l))h+=`<h1>${inl(l.slice(2))}</h1>`;
    else if(/^## /.test(l))h+=`<h2>${inl(l.slice(3))}</h2>`;
    else if(/^> /.test(l))h+=`<blockquote>${inl(l.slice(2))}</blockquote>`;
    else if(/^(\d+)\. /.test(l)){const it=[];while(i<L.length&&/^\d+\. /.test(L[i])){it.push(L[i].replace(/^\d+\. /,''));i++;}h+=`<ol>${it.map(x=>`<li>${inl(x)}</li>`).join('')}</ol>`;continue;}
    else if(/^- /.test(l)){const it=[];while(i<L.length&&/^- /.test(L[i])){it.push(L[i].slice(2));i++;}h+=`<ul>${it.map(x=>`<li>${inl(x)}</li>`).join('')}</ul>`;continue;}
    else if(l.trim())h+=`<p>${inl(l)}</p>`;
    i++;}
  return h;
}

// ── MAP APP ──
let leafPromise=null;
function loadLeaflet(){
  if(window.L)return Promise.resolve();
  if(leafPromise)return leafPromise;
  leafPromise=new Promise((res,rej)=>{
    const css=document.createElement('link');css.rel='stylesheet';css.href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css';document.head.appendChild(css);
    const s=document.createElement('script');s.src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js';s.onload=res;s.onerror=rej;document.head.appendChild(s);
  });
  return leafPromise;
}
async function renderMap(body){
  body.classList.add('nopad');
  body.innerHTML=`<div class="map-wrap"><div id="efbMap"></div>
    <div class="map-legend"><div class="row"><span class="dot" style="background:var(--accent-2)"></span>You</div>
    <div class="row"><span class="dot" style="background:#22C55E"></span>Departure</div>
    <div class="row"><span class="dot" style="background:#EF4444"></span>Arrival</div>
    <div class="row"><span class="dot" style="background:#F0A02A"></span>Traffic</div></div></div>`;
  try{ await loadLeaflet(); }catch{ body.innerHTML=`<div class="empty">The map needs an internet connection to load tiles.<br>Your route and traffic will appear once online.</div>`; return; }
  const start=(state.latitude||state.longitude)?[state.latitude,state.longitude]:[48,8];
  const map=L.map('efbMap',{zoomControl:false,attributionControl:true}).setView(start,5);
  L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',{maxZoom:18,
    attribution:'&copy; OSM &copy; CARTO'}).addTo(map);
  const layers={route:null,dep:null,arr:null,own:null,traffic:[]};
  function planeIcon(color,hdg){ return L.divIcon({className:'ac-marker',iconSize:[26,26],iconAnchor:[13,13],
    html:`<svg width="26" height="26" viewBox="0 0 24 24" style="transform:rotate(${hdg||0}deg)"><path fill="${color}" d="M12 2l3 8 7 3-7 1.5L12 22l-3-7.5L2 13l7-3z"/></svg>`}); }
  function drawRoute(){
    if(!plan.hasPlan||!plan.waypoints||!plan.waypoints.length)return;
    const pts=plan.waypoints.filter(w=>w.latitude||w.longitude).map(w=>[w.latitude,w.longitude]);
    if(pts.length>1){ layers.route=L.polyline(pts,{color:getComputedStyle(document.documentElement).getPropertyValue('--accent').trim()||'#3D7EEE',weight:2.5,opacity:.75}).addTo(map);
      layers.dep=L.circleMarker(pts[0],{radius:6,color:'#22C55E',fillColor:'#22C55E',fillOpacity:1}).addTo(map).bindTooltip(plan.departureIcao||'DEP');
      layers.arr=L.circleMarker(pts[pts.length-1],{radius:6,color:'#EF4444',fillColor:'#EF4444',fillOpacity:1}).addTo(map).bindTooltip(plan.arrivalIcao||'ARR');
      map.fitBounds(layers.route.getBounds(),{padding:[50,50],maxZoom:8}); }
  }
  function drawOwn(){
    if(!(state.latitude||state.longitude))return;
    const c=getComputedStyle(document.documentElement).getPropertyValue('--accent-2').trim()||'#5EA0FF';
    if(layers.own)layers.own.setLatLng([state.latitude,state.longitude]).setIcon(planeIcon(c,state.trackDeg));
    else layers.own=L.marker([state.latitude,state.longitude],{icon:planeIcon(c,state.trackDeg),zIndexOffset:1000}).addTo(map).bindTooltip(state.callsign||'You');
  }
  async function drawTraffic(){
    let t=[]; try{ t=await getJSON('/efb/api/traffic'); }catch{return;}
    layers.traffic.forEach(m=>map.removeLayer(m)); layers.traffic=[];
    t.forEach(p=>{ if(p.pilotId===msg.myPilotId)return;
      const m=L.marker([p.lat,p.lon],{icon:planeIcon('#F0A02A',p.heading)}).addTo(map)
        .bindTooltip(`${p.callsign||p.pilotName||'Traffic'} · ${p.phase||''}<br>${p.departureIcao||'?'}→${p.arrivalIcao||'?'} · ${p.alt} ft`);
      layers.traffic.push(m); });
  }
  drawRoute(); drawOwn(); drawTraffic();
  const iv=setInterval(()=>{drawOwn();drawTraffic();},12000);
  const liveIv=setInterval(drawOwn,2000);
  appCleanup=()=>{ clearInterval(iv); clearInterval(liveIv); try{map.remove();}catch{} };
}

// ── OFP APP (SimBrief PDF) ──
async function renderOfp(body){
  let o; try{ o=await getJSON('/efb/api/ofp'); }catch{ o={hasPdf:false}; }
  if(o.hasPdf&&o.pdfUrl){
    body.classList.add('nopad');
    body.innerHTML=`<div style="padding:10px 14px;display:flex;gap:10px">
      <a class="btn-ghost" href="${o.pdfUrl}" target="_blank" rel="noopener">Open in browser ↗</a></div>
      <div style="position:absolute;inset:56px 0 0"><iframe class="ofp-frame" src="${o.pdfUrl}"></iframe></div>`;
  }else{
    body.innerHTML=`<div class="empty">No OFP PDF available yet.<br>Import a SimBrief flight plan (in the ACARS app, or from EFB Settings) to load the briefing package.</div>`;
  }
}

// ── ATIS APP ──
let atisIcao=null;
function renderAtis(body){
  const dep=plan.departureIcao||state.departureIcao||'';
  const arr=plan.arrivalIcao||state.arrivalIcao||'';
  if(!atisIcao) atisIcao=arr||dep||'';
  body.innerHTML=`
    <div class="msg-tabs" id="atabs">
      ${dep?`<span class="t ${atisIcao===dep?'active':''}" data-i="${dep}">${dep} · DEP</span>`:''}
      ${arr?`<span class="t ${atisIcao===arr?'active':''}" data-i="${arr}">${arr} · ARR</span>`:''}
    </div>
    <div class="addfriend"><input id="aicao" maxlength="4" placeholder="ICAO" value="${esc(atisIcao)}">
      <button class="btn-primary" id="aget" style="width:auto;padding:12px 18px">Get ATIS</button></div>
    <div id="aout"><div class="empty">Select an airport to pull the ATIS.</div></div>`;
  $$('#atabs .t').forEach(t=>t.onclick=()=>{atisIcao=t.dataset.i;renderAtis($('#appBody'));loadAtis();});
  $('#aget').onclick=()=>{atisIcao=($('#aicao').value||'').trim().toUpperCase();loadAtis();};
  if(atisIcao) loadAtis();
}
async function loadAtis(){
  const out=$('#aout'); if(!out||!atisIcao)return;
  out.innerHTML=`<div class="empty">Fetching ATIS for ${esc(atisIcao)}…</div>`;
  let d; try{ d=await getJSON('/efb/api/atis?icao='+encodeURIComponent(atisIcao)); }
  catch{ out.innerHTML=`<div class="empty">Could not reach the ATIS service.</div>`; return; }
  if(!d.found){ out.innerHTML=`<div class="empty">No ATIS or METAR available for ${esc(d.icao||atisIcao)}.</div>`; return; }
  const src=d.source==='VATSIM'?'VATSIM · live controller ATIS':'Generated from METAR';
  out.innerHTML=`<div class="panel">
      <div class="panel-h"><span>${esc(d.icao)} ATIS</span><span class="panel-tag">${esc(src)}</span></div>
      ${d.letter?`<div class="atis-letter">${esc(d.letter)}</div>`:''}
      <pre class="atis-text">${esc(d.text)}</pre></div>
    ${d.metar?`<div class="panel" style="margin-top:12px"><div class="panel-h"><span>METAR</span></div><pre class="atis-text">${esc(d.metar)}</pre></div>`:''}
    ${d.taf?`<div class="panel" style="margin-top:12px"><div class="panel-h"><span>TAF</span></div><pre class="atis-text">${esc(d.taf)}</pre></div>`:''}`;
}

// ── ACARS APP (Hoppies message log) ──
async function renderAcars(body){
  body.innerHTML=`<div class="empty">Loading ACARS log…</div>`;
  let d; try{ d=await getJSON('/efb/api/hoppies'); }
  catch{ body.innerHTML=`<div class="empty">Could not load the ACARS log.</div>`; return; }
  if(!d.enabled){
    body.innerHTML=`<div class="empty">Hoppies ACARS is disabled.<br>Enable it in the desktop app under Settings → Hoppies.</div>`; return;
  }
  if(!d.messages||!d.messages.length){
    body.innerHTML=`<div class="empty">No ACARS messages sent this session.<br>Loadsheets, timing reminders and position calls will appear here.</div>`; return;
  }
  body.innerHTML=`<div class="acars-head">Logon <b>${esc(d.pilotCode||'—')}</b> · ${d.messages.length} message${d.messages.length===1?'':'s'} this session</div>`
    + d.messages.map((m,i)=>`<div class="acars-row" data-i="${i}">
        <div class="acars-top">
          <span class="acars-dot ${m.success?'ok':'fail'}"></span>
          <span class="acars-cat">${esc(m.category||'ACARS')}</span>
          <span class="acars-time">${fmtT(m.sentAtUtc)}</span>
        </div>
        <div class="acars-body">${esc(m.message)}</div></div>`).join('');
  $$('#appBody .acars-row').forEach(r=>r.onclick=()=>r.classList.toggle('open'));
}

// ── MESSAGES APP (same system as the desktop app) ──
let msgTab='friends', openThreadId=null;
function renderMessages(body){
  if(openThreadId){ renderThread(body,openThreadId); return; }
  body.innerHTML=`
    <div class="mycode"><div class="lbl">My Friend Code</div><div class="code">${msg.myFriendCode||'— set ACARS key —'}</div></div>
    <div class="addfriend"><input id="fcode" maxlength="9" placeholder="FRIEND CODE"><button class="btn-primary" id="addFr" style="width:auto;padding:12px 18px">Add</button></div>
    <div class="msg-tabs"><span class="t ${msgTab==='friends'?'active':''}" data-t="friends">Friends</span>
      <span class="t ${msgTab==='broadcast'?'active':''}" data-t="broadcast">Broadcast</span></div>
    <div id="mbody"></div>`;
  $('#addFr').onclick=async()=>{ const c=$('#fcode').value.trim(); if(!c)return;
    const r=await postJSON('/efb/api/friends/add',{code:c}).catch(()=>({error:'failed'}));
    if(r.error)alert(r.error); else { $('#fcode').value=''; msg=await getJSON('/efb/api/messages').catch(()=>msg); renderMessages($('#appBody')); } };
  $$('#appBody .msg-tabs .t').forEach(t=>t.onclick=()=>{msgTab=t.dataset.t;renderMessages($('#appBody'));});
  if(msgTab==='friends')renderFriends(); else renderBroadcast();
}
function renderFriends(){
  const mb=$('#mbody');
  if(!msg.friends.length){ mb.innerHTML=`<div class="empty">No friends yet. Add a pilot by their Friend Code above.</div>`; return; }
  mb.innerHTML=msg.friends.map(f=>{
    const unread=msg.messages.filter(m=>!m.mine&&!m.isRead&&m.senderId===f.pilotId).length;
    return `<div class="friend" data-id="${f.pilotId}"><div class="fr-av">${initials(f.pilotName)}</div>
      <div class="fr-info"><div class="fr-name">${esc(f.pilotName||'Pilot')}</div><div class="fr-sub">${f.rank||''} ${f.friendCode||''}</div></div>
      ${unread?`<span class="badge" style="position:static">${unread}</span>`:(f.isOnline?`<span class="fr-on"></span>`:'')}</div>`;
  }).join('');
  $$('#mbody .friend').forEach(el=>el.onclick=()=>{ openThreadId=el.dataset.id; renderMessages($('#appBody')); });
}
function renderBroadcast(){
  const mb=$('#mbody');
  const bc=msg.messages.filter(m=>m.type==='Broadcast');
  mb.innerHTML=`<div class="thread"><div class="thread-msgs" id="tmsgs">${
    bc.length?bc.map(bubble).join(''):`<div class="empty">No broadcasts yet.</div>`}</div>
    <form class="composer" id="bc"><input id="bctext" placeholder="Broadcast to all pilots…"><button class="btn-primary" type="submit">Send</button></form></div>`;
  $('#bc').onsubmit=async e=>{e.preventDefault();const t=$('#bctext').value.trim();if(!t)return;$('#bctext').value='';
    await postJSON('/efb/api/messages/send',{broadcast:true,content:t}).catch(()=>{}); };
  scrollThread();
}
function renderThread(body,fid){
  const f=msg.friends.find(x=>x.pilotId===fid)||{pilotName:'Pilot',pilotId:fid};
  const conv=msg.messages.filter(m=>m.type!=='Broadcast'&&((m.senderId===fid)||(m.mine&&m.recipientId===fid)));
  // mark their unread as read
  conv.filter(m=>!m.mine&&!m.isRead).forEach(m=>postJSON('/efb/api/messages/read',{id:m.id}).catch(()=>{}));
  $('#appName').textContent=f.pilotName||'Pilot';
  $('#appActions').innerHTML=`<button class="btn-ghost" id="thBack">‹ Messages</button>`;
  $('#thBack').onclick=()=>{openThreadId=null;$('#appName').textContent='Messages';$('#appActions').innerHTML='';renderMessages($('#appBody'));};
  body.innerHTML=`<div class="thread"><div class="thread-msgs" id="tmsgs">${
    conv.length?conv.map(bubble).join(''):`<div class="empty">No messages yet. Say hello 👋</div>`}</div>
    <form class="composer" id="dm"><input id="dmtext" placeholder="Message ${esc(f.pilotName||'pilot')}…"><button class="btn-primary" type="submit">Send</button></form></div>`;
  $('#dm').onsubmit=async e=>{e.preventDefault();const t=$('#dmtext').value.trim();if(!t)return;$('#dmtext').value='';
    await postJSON('/efb/api/messages/send',{recipientId:fid,content:t}).catch(()=>{}); };
  scrollThread();
}
function bubble(m){ return `<div class="bubble ${m.mine?'me':'them'}">${m.mine?'':`<div class="who">${esc(m.senderName||'Pilot')}</div>`}
  <div>${esc(m.content)}</div><div class="t">${fmtT(m.sentAt)}</div></div>`; }
function scrollThread(){ const t=$('#tmsgs'); if(t)t.scrollTop=t.scrollHeight; }
function initials(n){ n=(n||'').trim(); if(!n)return'?'; const p=n.split(/\s+/); return (p[0][0]+(p[1]?p[1][0]:'')).toUpperCase(); }

// ── LANDING REPORT APP ──
// Renders the SHARED LandingReport projection, so it matches the desktop
// touchdown modal exactly. Reads persisted history, so nothing is ever lost.
let landingSel=0;
async function renderLanding(body){
  body.innerHTML=`<div class="empty">Loading landing report…</div>`;
  let d; try{ d=await getJSON('/efb/api/landings'); }
  catch{ body.innerHTML=`<div class="empty">Could not load landing reports.</div>`; return; }
  if(!d.hasLanding){ body.innerHTML=`<div class="empty">No landings recorded yet.<br>Your touchdown report will appear here after you land.</div>`; return; }
  const hist=d.history||[], r=hist[landingSel]||d.latest;
  body.innerHTML=`
    ${hist.length>1?`<div class="msg-tabs lr-hist" id="lrHist">${hist.slice(0,8).map((h,i)=>
      `<span class="t ${i===landingSel?'active':''}" data-i="${i}">${esc(h.airportIcao)} ${h.score}</span>`).join('')}</div>`:''}
    <div class="lr-card">
      <div class="lr-head">
        <div><div class="lr-chip">TOUCHDOWN REPORT</div>
          <div class="lr-apt">${esc(r.airportIcao)} <span class="lr-dot">·</span> ${esc(r.runway)}</div>
          <div class="lr-sub">${[r.flightNumber,r.route,r.aircraftType].filter(Boolean).map(esc).join(' · ')||'—'}</div></div>
        <div class="lr-zulu">${fmtT(r.timestampUtc)}</div>
      </div>
      <div class="lr-hero">
        <div class="lr-ring" style="--c:${r.gradeColor};--p:${r.score}">
          <div class="lr-ring-in"><span class="lr-score">${r.score}</span><span class="lr-den">/100</span></div>
        </div>
        <div class="lr-gcol">
          <div class="lr-grade" style="color:${r.gradeColor};border-color:${r.gradeColor};background:color-mix(in srgb,${r.gradeColor} 14%,transparent)">${esc(r.grade)}</div>
          <div class="lr-butter" style="border-left-color:${r.butterColor}">
            <div class="lr-brate" style="color:${r.butterColor}">${esc(r.butterRating)}</div>
            <div class="lr-bblurb">${esc(r.butterBlurb)}</div>
            <div class="lr-bvs"><span style="color:${r.butterColor}">${num(Math.abs(r.verticalSpeedFpm))}</span> fpm touchdown</div>
          </div>
          <div class="lr-flags">
            <span class="lr-flag ${r.stableApproach?'ok':'bad'}">${r.stableApproach?'✓':'✗'} Stable Approach</span>
            <span class="lr-flag ${r.flareDetected?'ok':'neu'}">${r.flareDetected?'✓':'–'} Flare</span>
            <span class="lr-flag ${r.bounceCount>0?'bad':'ok'}">${r.bounceCount>0?'! '+r.bounceCount+' Bounce'+(r.bounceCount>1?'s':''):'✓ No Bounce'}</span>
            ${r.isPersonalBest?`<span class="lr-flag best">★ Personal Best</span>`
              :(r.totalLandings>1?`<span class="lr-flag neu"># ${r.rankAmongLandings} of ${r.totalLandings} · avg ${r.averageScore}</span>`:'')}
          </div>
        </div>
      </div>
      <div class="lr-strip"></div>
      <div class="lr-params">
        ${[['VERT SPEED',num(Math.abs(r.verticalSpeedFpm)),'fpm',Math.abs(r.verticalSpeedFpm)>600],
           ['AIRSPEED',num(r.iasKts),'kt',false],
           ['PITCH',num(r.pitchDeg,1),'°',false],
           ['BANK',num(r.bankDeg,1),'°',r.bankDeg>3],
           ['HEADWIND',num(r.headwindKts),'kt',false],
           ['CROSSWIND',num(r.crosswindKts),'kt',false]]
          .map(([l,v,u,w])=>`<div class="lr-p ${w?'warn':''}"><span class="pv">${v}</span><span class="pu">${u}</span><span class="pl">${l}</span></div>`).join('')}
      </div>
      <div class="lr-bd">
        <div class="lr-bd-h">SCORE BREAKDOWN</div>
        ${r.factors.map(f=>`<div class="lr-f"><span class="fl">${esc(f.label)}</span>
          <div class="ft"><div class="ff" style="width:${f.pct.toFixed(1)}%;background:${f.color}"></div></div>
          <span class="fp" style="color:${f.color}">${num(f.score)}<span class="fm">/${num(f.max)}</span></span></div>`).join('')}
      </div>
      <div class="lr-foot">
        ${r.rolloutFt>10?`<div class="lr-fs"><div class="v">${num(r.rolloutFt)}</div><div class="l">ft rollout</div></div>`:''}
        ${r.thresholdFt>10?`<div class="lr-fs"><div class="v">${num(r.thresholdFt)}</div><div class="l">ft past threshold</div></div>`:''}
        ${r.windSpeedKts>0?`<div class="lr-fs"><div class="v">${num(r.windDirectionDeg)}°/${num(r.windSpeedKts)}</div><div class="l">surface wind</div></div>`:''}
        <div class="lr-fs"><div class="v">${num(r.fuelLbs)}</div><div class="l">lbs fuel</div></div>
      </div>
    </div>`;
  $$('#lrHist .t').forEach(t=>t.onclick=()=>{landingSel=+t.dataset.i;renderLanding($('#appBody'));});
}

// ── SCRATCHPAD APP (draw like paper, or type) ──
let scratchMode=localStorage.getItem('efbScratchMode')||'draw';
function renderScratch(body){
  body.classList.add('nopad');
  body.innerHTML=`
    <div class="scr-bar">
      <div class="seg"><button class="seg-btn ${scratchMode==='draw'?'active':''}" data-m="draw">Draw</button>
        <button class="seg-btn ${scratchMode==='type'?'active':''}" data-m="type">Type</button></div>
      <div class="scr-tools ${scratchMode==='draw'?'':'hidden'}" id="scrTools">
        ${['#0A0D17','#3D7EEE','#EF4444','#22C55E'].map((c,i)=>`<span class="pen ${i===0?'active':''}" data-c="${c}" style="background:${c}"></span>`).join('')}
        <span class="pen-size" data-s="2">·</span><span class="pen-size active" data-s="4">•</span><span class="pen-size" data-s="9">⬤</span>
        <button class="btn-ghost" id="scrUndo">Undo</button>
      </div>
      <button class="btn-ghost" id="scrClear">Clear</button>
    </div>
    <div class="scr-stage">
      <canvas id="scrCanvas" class="${scratchMode==='draw'?'':'hidden'}"></canvas>
      <textarea id="scrText" class="scr-text ${scratchMode==='type'?'':'hidden'}"
        placeholder="Clearances, squawks, ATIS letter, runway assignment…"></textarea>
    </div>`;
  $$('#appBody .seg-btn').forEach(b=>b.onclick=()=>{ scratchMode=b.dataset.m;
    localStorage.setItem('efbScratchMode',scratchMode); saveScratch(); renderScratch($('#appBody')); });
  $('#scrClear').onclick=()=>{ if(scratchMode==='type'){ $('#scrText').value=''; }
    else { const c=$('#scrCanvas'); c.getContext('2d').clearRect(0,0,c.width,c.height); strokes=[]; } saveScratch(); };
  const ta=$('#scrText'); ta.value=localStorage.getItem('efbScratchText')||'';
  ta.oninput=saveScratch;
  if(scratchMode==='draw') initScratchCanvas();
  appCleanup=saveScratch;
}
let strokes=[], penColor='#0A0D17', penSize=4;
function initScratchCanvas(){
  const cv=$('#scrCanvas'), stage=cv.parentElement;
  const dpr=window.devicePixelRatio||1;
  cv.width=stage.clientWidth*dpr; cv.height=stage.clientHeight*dpr;
  cv.style.width=stage.clientWidth+'px'; cv.style.height=stage.clientHeight+'px';
  const g=cv.getContext('2d'); g.scale(dpr,dpr); g.lineCap='round'; g.lineJoin='round';
  try{ strokes=JSON.parse(localStorage.getItem('efbScratchDraw')||'[]'); }catch{ strokes=[]; }
  redraw();
  function redraw(){ g.clearRect(0,0,cv.width,cv.height);
    strokes.forEach(s=>{ g.beginPath(); g.strokeStyle=s.c; g.lineWidth=s.w;
      s.p.forEach((pt,i)=>i?g.lineTo(pt[0],pt[1]):g.moveTo(pt[0],pt[1])); g.stroke(); }); }
  let cur=null;
  const pos=e=>{ const r=cv.getBoundingClientRect(); const t=e.touches?e.touches[0]:e;
    return [t.clientX-r.left, t.clientY-r.top]; };
  const down=e=>{ e.preventDefault(); cur={c:penColor,w:penSize,p:[pos(e)]}; strokes.push(cur); };
  const move=e=>{ if(!cur)return; e.preventDefault(); cur.p.push(pos(e)); redraw(); };
  const up=()=>{ if(cur){cur=null; saveScratch();} };
  cv.addEventListener('pointerdown',down); cv.addEventListener('pointermove',move);
  window.addEventListener('pointerup',up);
  $$('#scrTools .pen').forEach(p=>p.onclick=()=>{ penColor=p.dataset.c;
    $$('#scrTools .pen').forEach(x=>x.classList.remove('active')); p.classList.add('active'); });
  $$('#scrTools .pen-size').forEach(p=>p.onclick=()=>{ penSize=+p.dataset.s;
    $$('#scrTools .pen-size').forEach(x=>x.classList.remove('active')); p.classList.add('active'); });
  $('#scrUndo').onclick=()=>{ strokes.pop(); redraw(); saveScratch(); };
}
function saveScratch(){
  try{
    const ta=$('#scrText'); if(ta) localStorage.setItem('efbScratchText',ta.value);
    localStorage.setItem('efbScratchDraw',JSON.stringify(strokes.slice(-400)));
  }catch{}
}

// ── CABIN PA APP ──
const PA_KEYS=[['boarding','Boarding'],['welcome','Welcome / Pushback'],['cruise','Top of Climb'],
  ['descent','Descent'],['landing','After Landing'],['deboard','Disembarking']];
let paSel='boarding';
function renderPa(body){
  const s=state||{};
  body.innerHTML=`<div class="msg-tabs pa-tabs">${PA_KEYS.map(([k,l])=>
      `<span class="t ${paSel===k?'active':''}" data-k="${k}">${l}</span>`).join('')}</div>
    <div class="panel"><div class="panel-h"><span>Announcement</span>
      <button class="btn-ghost" id="paCopy">Copy</button></div>
      <div class="pa-script" id="paScript">${esc(paScript(paSel,s))}</div></div>
    <p class="set-note">Auto-written from your live flight data. Read it as-is or make it your own.</p>`;
  $$('#appBody .pa-tabs .t').forEach(t=>t.onclick=()=>{paSel=t.dataset.k;renderPa($('#appBody'));});
  $('#paCopy').onclick=async()=>{ try{ await navigator.clipboard.writeText($('#paScript').textContent);
    $('#paCopy').textContent='Copied'; setTimeout(()=>{const b=$('#paCopy'); if(b)b.textContent='Copy';},1500);}catch{} };
}
function paScript(k,s){
  const cs=s.callsign||s.flightNumber||'AviatesAir', dep=s.departureName||s.departureIcao||'our departure airport',
    arr=s.arrivalName||s.arrivalIcao||'our destination',
    fl=s.altitudeFt>=18000?'flight level '+Math.round(s.altitudeFt/100):(plan.cruiseAltitudeFt?'flight level '+Math.round(plan.cruiseAltitudeFt/100):'our cruising altitude'),
    ete=s.eteMinutes>0?etaText(s):(plan.plannedFlightTimeMin?Math.floor(plan.plannedFlightTimeMin/60)+'h '+(plan.plannedFlightTimeMin%60)+'m':'a short time'),
    wx=plan.arrivalMetar?(()=>{const m=parseMetar(plan.arrivalMetar);return m.tempC!=null?`around ${m.tempC} degrees celsius`:'pleasant';})():'pleasant';
  const T={
    boarding:`Good day ladies and gentlemen, and welcome aboard AviatesAir flight ${cs}, with service to ${arr}.\n\nPlease take your seat as quickly as possible and stow your cabin baggage in the overhead lockers or completely under the seat in front of you. Please keep the aisle clear to help us get away on time.\n\nOn behalf of the flight deck and your cabin crew, thank you for choosing AviatesAir.`,
    welcome:`Ladies and gentlemen, this is your captain speaking. Welcome aboard AviatesAir ${cs} from ${dep} to ${arr}.\n\nOur flight time today will be approximately ${ete}, and we'll be cruising at ${fl}. Cabin crew, please prepare doors for departure and cross-check.\n\nPlease ensure your seatbelt is fastened and your seat back and tray table are in the upright position. Sit back and enjoy the flight.`,
    cruise:`Ladies and gentlemen, from the flight deck — we have now reached our cruising altitude of ${fl}.\n\nI've switched off the seatbelt sign, however we do recommend keeping your belt loosely fastened whenever seated. Our cabin crew will shortly be coming through the cabin with refreshments.\n\nExpected arrival into ${arr} is in about ${ete}.`,
    descent:`Ladies and gentlemen, we have begun our descent into ${arr}.\n\nThe seatbelt sign is now on — please return to your seat, fasten your seatbelt, and ensure your tray table and seat back are stowed. Cabin crew will be through the cabin to collect any remaining items.\n\nThe weather at ${arr} is ${wx}. We expect to be on the ground shortly.`,
    landing:`Ladies and gentlemen, welcome to ${arr}.\n\nPlease remain seated with your seatbelt fastened until the aircraft has come to a complete stop at the gate and the seatbelt sign has been switched off.\n\nOn behalf of AviatesAir and the entire crew, thank you for flying with us today.`,
    deboard:`Ladies and gentlemen, we have arrived at our stand here in ${arr}.\n\nPlease take care when opening the overhead lockers as items may have shifted during the flight, and please check around your seat for personal belongings.\n\nWe hope to see you again soon on board AviatesAir. Goodbye.`,
  };
  return T[k]||'';
}

// ── MINI GAME: "Approach!" ──
// Deliberately tiny: one canvas, rAF only while the app is open, and the loop
// is torn down by appCleanup the moment you go home (no background CPU).
function renderGame(body){
  body.classList.add('nopad');
  const best=+(localStorage.getItem('efbGameBest')||0);
  body.innerHTML=`<div class="game-wrap"><canvas id="gcv"></canvas>
    <div class="game-hud"><span id="gscore">0</span><span class="gbest">BEST ${best}</span></div>
    <div class="game-over" id="gover"><div class="go-card"><div class="go-t" id="goT">APPROACH!</div>
      <div class="go-s" id="goS">Tap or press space to keep the aircraft on the glideslope.<br>Avoid the terrain.</div>
      <button class="btn-primary" id="goBtn" style="width:auto;padding:12px 26px">Start</button></div></div></div>`;
  const cv=$('#gcv'), stage=cv.parentElement, g=cv.getContext('2d');
  let W=0,H=0,dpr=window.devicePixelRatio||1;
  function size(){ W=stage.clientWidth; H=stage.clientHeight;
    cv.width=W*dpr; cv.height=H*dpr; cv.style.width=W+'px'; cv.style.height=H+'px';
    g.setTransform(dpr,0,0,dpr,0,0); }
  size();
  let y,vy,gates,score,alive=false,raf=null,t=0;
  const GAP=150, SPD=2.4;
  function reset(){ y=H/2; vy=0; gates=[]; score=0; t=0; alive=true;
    for(let i=0;i<3;i++) gates.push({x:W+i*260, gy:80+Math.random()*(H-160-GAP)}); }
  function flap(){ if(alive) vy=-4.6; }
  cv.addEventListener('pointerdown',e=>{e.preventDefault();flap();});
  const key=e=>{ if(e.code==='Space'){e.preventDefault();flap();} };
  window.addEventListener('keydown',key);
  function loop(){
    if(!alive){ raf=null; return; }
    t++; vy+=0.22; y+=vy;
    g.clearRect(0,0,W,H);
    // sky
    const grd=g.createLinearGradient(0,0,0,H); grd.addColorStop(0,'#12213a'); grd.addColorStop(1,'#05070d');
    g.fillStyle=grd; g.fillRect(0,0,W,H);
    const accent=getComputedStyle(document.documentElement).getPropertyValue('--accent').trim()||'#3D7EEE';
    // gates (terrain)
    gates.forEach(o=>{ o.x-=SPD;
      g.fillStyle='#1d2b45'; g.strokeStyle=accent; g.lineWidth=2;
      g.fillRect(o.x,0,56,o.gy); g.strokeRect(o.x,0,56,o.gy);
      g.fillRect(o.x,o.gy+GAP,56,H-o.gy-GAP); g.strokeRect(o.x,o.gy+GAP,56,H-o.gy-GAP);
      if(!o.done&&o.x+56<60){ o.done=true; score++; $('#gscore').textContent=score; }
    });
    if(gates[0].x<-60){ gates.shift(); gates.push({x:gates[gates.length-1].x+260, gy:80+Math.random()*(H-160-GAP)}); }
    // aircraft
    g.save(); g.translate(60,y); g.rotate(Math.max(-0.5,Math.min(0.9,vy/12)));
    g.fillStyle=accent; g.beginPath(); g.moveTo(14,0); g.lineTo(-10,-8); g.lineTo(-5,0); g.lineTo(-10,8); g.closePath(); g.fill();
    g.restore();
    // collide
    const hit=gates.some(o=>60+12>o.x&&60-10<o.x+56&&(y-8<o.gy||y+8>o.gy+GAP));
    if(hit||y>H||y<0) return end();
    raf=requestAnimationFrame(loop);
  }
  function end(){ alive=false; if(raf)cancelAnimationFrame(raf); raf=null;
    const b=+(localStorage.getItem('efbGameBest')||0);
    if(score>b) localStorage.setItem('efbGameBest',score);
    $('#goT').textContent=score>b?'NEW BEST!':'GO AROUND';
    $('#goS').innerHTML=`You cleared <b>${score}</b> gate${score===1?'':'s'}.<br>Best ${Math.max(score,b)}`;
    $('#goBtn').textContent='Try again'; $('#gover').classList.remove('playing'); }
  $('#goBtn').onclick=()=>{ size(); reset(); $('#gscore').textContent='0';
    $('#gover').classList.add('playing'); if(!raf) raf=requestAnimationFrame(loop); };
  appCleanup=()=>{ alive=false; if(raf)cancelAnimationFrame(raf); window.removeEventListener('keydown',key); };
}

// ── SETTINGS APP ──
const THEMES=[{id:'aviates',name:'Aviates',c1:'#3D7EEE',c2:'#0A0D17'},{id:'boeing',name:'Boeing',c1:'#2ec5c9',c2:'#05080d'},
  {id:'airbus',name:'Airbus',c1:'#1e88e5',c2:'#0a0f16'},{id:'minimal',name:'Minimal',c1:'#c9ced8',c2:'#0c0d10'}];
function renderSettings(body){
  body.innerHTML=`
    <div class="set-group"><h3>Wallpaper</h3>
      <div class="wall-preview"></div>
      <div class="row-btns"><label class="btn-primary" style="width:auto;padding:12px 18px;cursor:pointer">Upload image<input id="wpFile" type="file" accept="image/*" hidden></label>
      <button class="btn-ghost" id="wpReset">Reset to default</button></div></div>
    <div class="set-group"><h3>Appearance</h3>
      <div class="theme-picker">${THEMES.map(t=>`<div class="theme-swatch ${t.id===currentTheme?'active':''}" data-theme="${t.id}" style="background:linear-gradient(150deg,${t.c1},${t.c2})">${t.name}</div>`).join('')}</div>
      <div class="mode-toggle"><button data-mode="dark" class="${currentMode==='dark'?'active':''}">Dark</button><button data-mode="light" class="${currentMode==='light'?'active':''}">Light</button></div></div>
    <div class="set-group"><h3>Flight Plan (EFB)</h3>
      <p class="set-note" style="margin:0 0 12px">${plan.hasPlan?(plan.fromEfbImport?'Imported in the EFB: ':'From ACARS: ')+plan.departureIcao+' → '+plan.arrivalIcao:'No plan loaded. If you import in the ACARS app it appears here automatically.'}</p>
      <div class="addfriend"><input id="sbUser" placeholder="SimBrief username / pilot ID" style="text-transform:none;letter-spacing:0"><button class="btn-primary" id="sbImport" style="width:auto;padding:12px 18px">Import</button></div>
      <p class="set-note">Importing here fills the EFB only — it does not change your ACARS flight.</p></div>
    <div class="set-group"><h3>Flight</h3>
      <button class="btn-danger" id="endFlight">End Flight</button>
      <p class="set-note">Ends the active flight and files the PIREP — same as the desktop button.</p></div>
    <div class="set-group"><h3>Connection</h3>
      <div class="conn-info">Host: <b>${location.host}</b><br>Transport: <b id="tp">—</b><br>Pilot: <b>${esc(msg.myPilotName||'—')}</b></div></div>`;
  $('#wpFile').onchange=async e=>{ const file=e.target.files[0]; if(!file)return;
    const buf=await file.arrayBuffer();
    await fetch('/efb/api/wallpaper',{method:'POST',headers:{'X-EFB-Token':token,'Content-Type':'application/octet-stream'},body:buf});
    await loadWallpaper(); renderSettings($('#appBody')); };
  $('#wpReset').onclick=async()=>{ await fetch('/efb/api/wallpaper',{method:'DELETE',headers:{'X-EFB-Token':token}});
    document.documentElement.removeAttribute('data-wallpaper'); renderSettings($('#appBody')); };
  $$('#appBody .theme-swatch').forEach(sw=>sw.onclick=()=>{setTheme(sw.dataset.theme);renderSettings($('#appBody'));});
  $$('#appBody .mode-toggle button').forEach(b=>b.onclick=()=>{setMode(b.dataset.mode);renderSettings($('#appBody'));});
  $('#sbImport').onclick=async()=>{ const u=$('#sbUser').value.trim();
    $('#sbImport').textContent='…';
    const r=await postJSON('/efb/api/flightplan/import',{username:u}).catch(()=>({error:'failed'}));
    if(r.error){alert(r.error);$('#sbImport').textContent='Import';} else { plan=r; renderSettings($('#appBody')); } };
  $('#endFlight').onclick=async()=>{ if(confirm('End the active flight and file the PIREP?')) await postJSON('/efb/api/flight/end',{}).catch(()=>{}); };
  setInterval(()=>{const e=$('#tp');if(e)e.textContent=(ws&&ws.readyState===1)?'WebSocket (live)':'Polling';},1000);
}

// ── theme / mode ──
let currentTheme='aviates',currentMode='dark';
function loadPrefs(){
  currentTheme=localStorage.getItem('efbTheme')||'aviates'; currentMode=localStorage.getItem('efbMode')||'dark';
  document.documentElement.dataset.theme=currentTheme; document.documentElement.dataset.mode=currentMode;
  getJSON('/efb/api/theme').then(d=>{if(d.theme&&d.theme!==currentTheme)setTheme(d.theme);}).catch(()=>{});
}
function setTheme(id){ currentTheme=id; document.documentElement.dataset.theme=id; localStorage.setItem('efbTheme',id); postJSON('/efb/api/theme',{theme:id}).catch(()=>{}); }
function setMode(m){ currentMode=m; document.documentElement.dataset.mode=m; localStorage.setItem('efbMode',m); }

// go!
if(!token)showPairing(); else boot();
})();

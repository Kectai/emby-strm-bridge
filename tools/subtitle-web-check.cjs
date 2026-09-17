const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
for(const version of ['4.9.5.0','4.10.0.40']){
 const src=fs.readFileSync(require('node:path').join(process.argv[2] || '.local/subtitle-implementation', 'web-'+version+'.js'),'utf8');
 new vm.Script(src); // Parse the complete exact transformed module, not a synthetic anchor.
 const a=src.indexOf('function strmBridgeCandidate('),b=src.indexOf('function renderTracksEventsNative(',a);
 const imports=[],calls=[],selected=[];let fallback=0;
 const scope={window:{Worker:1,ReadableStream:1,AbortController:1,TextDecoder:1},Emby:{importModule:async p=>{imports.push(p);return{render:async o=>calls.push(o)}}},
 _browser:{default:{}},_htmlmediahelper:{default:{enableHlsJsPlayer:()=>true}},
 _connectionmanager:{default:{getApiClient:()=>({})}},renderTracksEventsNative:()=>fallback++,removeCueEvents:()=>{},setTrackForCustomDisplay:(i,v,t)=>selected.push(t)};
 vm.createContext(scope);vm.runInContext(src.slice(src.indexOf('function strmBridgeHlsCapable('),src.indexOf('function strmBridgeInstallProfile('))+src.slice(a,b),scope);
 const selection=src.indexOf('function setCurrentTrackElement('),next=src.indexOf('function startInitialSubtitleTrackTimeout(',selection);
 assert.ok(selection>0&&next>selection);
 vm.runInContext(src.slice(selection,next),scope);
 (async()=>{
 const instance={};const track={Codec:'ass',IsExternal:false,DeliveryMethod:'External'};const media={Container:'mkv',Protocol:'Http'};
 await scope.renderTracksEvents(instance,{},track,{},media);
 assert.equal(calls.length,1);assert.equal(fallback,0);
 await scope.renderTracksEvents(instance,{}, {...track,IsExternal:true},{},media);assert.equal(fallback,1);
 await scope.renderTracksEvents(instance,{}, {...track,Codec:'subrip'},{},media);assert.equal(calls.length,2);
 await scope.renderTracksEvents(instance,{},track,{}, {...media,Container:'m2ts'});assert.equal(fallback,2);
 scope._htmlmediahelper.default.enableHlsJsPlayer=()=>false;
 await scope.renderTracksEvents(instance,{},track,{},media);assert.equal(fallback,3);assert.equal(calls.length,2);
 scope._htmlmediahelper.default.enableHlsJsPlayer=()=>true;scope._browser.default.chromecast=true;
 await scope.renderTracksEvents(instance,{},track,{},media);assert.equal(fallback,4);assert.equal(calls.length,2);
 scope._browser.default.chromecast=false;
 let runtime;scope._htmlmediahelper.default.enableHlsJsPlayer=(value,type)=>{runtime=value;assert.equal(type,'Video');return true;};
 assert.equal(scope.strmBridgeCandidate(track,{...media,RunTimeTicks:123}),true);assert.equal(runtime,123);
 const subrip={...track,Codec:'subrip',Type:'Subtitle',Index:3};
 const element={textTracks:[{mode:'showing'},{mode:'showing'}]};
 scope.setCurrentTrackElement({setSubtitleOffset:()=>{}},element,3,{mediaSource:{...media,MediaStreams:[subrip]}});
 assert.equal(selected[0],subrip);assert.ok(element.textTracks.every(t=>t.mode==='disabled'));
 console.log('PASS transformed Web module syntax, automatic ASS/SubRip dispatch and native exclusion '+version);
 })().catch(e=>{console.error(e);process.exitCode=1});
}

// Exercise the real host's manual selection and delayed startup selection together.
// A pending startup choice must not remove the newly selected bridge subtitle.
async function checkStartupSelection(version) {
 const path=require('node:path');
 const src=fs.readFileSync(path.join(process.argv[2] || '.local/subtitle-implementation','web-'+version+'.js'),'utf8');
 let disposed=0,starts=0;
 const scope={window:{Worker:1,ReadableStream:1,AbortController:1,TextDecoder:1},console:{log(){}},
   isNativeLG:false,_browser:{default:{}},_htmlmediahelper:{default:{enableHlsJsPlayer:()=>true}},sortMediaStreamTextTracks:()=>0,getMediaStreamSubtitleTracks:s=>s.MediaStreams,
   removeCueEvents:()=>{},setCueAppearance:()=>{},enableChunkedResponse:()=>false,
   _connectionmanager:{default:{getApiClient:()=>({})}},
   Emby:{importModule:async()=>({render:async o=>{starts++;o.instance.strmBridgeSubtitle={dispose(){disposed++;}};}})},
   renderTracksEventsNative:()=>{},subtitleTrackIndexToSetOnPlaying:-1,initialSubtitleTrackTimeout:null,clearTimeout,
   HtmlVideoPlayer:function(){}};
 vm.createContext(scope);
 const wrapper=src.indexOf('function strmBridgeCandidate('),native=src.indexOf('function renderTracksEventsNative(',wrapper);
 vm.runInContext(src.slice(src.indexOf('function strmBridgeHlsCapable('),src.indexOf('function strmBridgeInstallProfile('))+src.slice(wrapper,native),scope);
 const destruction=src.indexOf('function destroyCustomTrack(');
 vm.runInContext(src.slice(destruction,src.indexOf('function ',destruction+10)),scope);
 const custom=src.indexOf('function setTrackForCustomDisplay('),timer=src.indexOf('function startInitialSubtitleTrackTimeout(',custom);
 vm.runInContext(src.slice(custom,timer),scope);
 const track={Index:0,Type:'Subtitle',Codec:'ass',IsExternal:false,DeliveryMethod:'External'};
 const instance={_mediaElement:{textTracks:[]},customTrackIndex:-1,setSubtitleOffset:()=>{},
   _currentPlayOptions:{item:{Id:'fixture'},mediaSource:{Id:'source',Container:'mkv',Protocol:'Http',MediaStreams:[track]}}};
 scope.self=instance;
 const setter=version.startsWith('4.9')?'self.setSubtitleStreamIndex=function(index){':'HtmlVideoPlayer.prototype.setSubtitleStreamIndex=function(index){';
 const at=src.indexOf(setter);assert.ok(at>=0);
 vm.runInContext(src.slice(at,src.indexOf('},',at)+1)+';',scope);
 const manual=version.startsWith('4.9')?instance.setSubtitleStreamIndex:scope.HtmlVideoPlayer.prototype.setSubtitleStreamIndex;
 manual.call(instance,0);await new Promise(resolve=>setImmediate(resolve));
 assert.equal(starts,1);
 const bound='this.boundonInitialSubtitleTrackTimeout=';const callbackAt=src.indexOf(bound)+bound.length;
 const callbackEnd=src.indexOf('}.bind(this)',callbackAt)+1;
 const callback=vm.runInContext('('+src.slice(callbackAt,callbackEnd)+')',scope);
 callback.call(instance);await new Promise(resolve=>setImmediate(resolve));
 assert.equal(disposed,0,'delayed startup selection removed the newly selected subtitle');
 assert.equal(instance.customTrackIndex,0);
 manual.call(instance,-1);
 assert.equal(disposed,1,'explicit subtitle off must still dispose');
 callback.call(instance);
 assert.equal(starts,1,'delayed callback must not reenable a disabled subtitle');
 let timers=0;
 scope.setTimeout=()=>{timers++;};
 const initial=src.indexOf('function startInitialSubtitleTrackTimeout('),audio=src.indexOf('function startInitialAudioTrackTimeout(',initial);
 vm.runInContext(src.slice(initial,audio),scope);
 scope.subtitleTrackIndexToSetOnPlaying=0;
 scope.startInitialSubtitleTrackTimeout(instance);await new Promise(resolve=>setImmediate(resolve));
 assert.equal(starts,2,'the first supported subtitle must start immediately');
 assert.equal(timers,0,'supported subtitles must not inherit the 400 ms native selection delay');
 console.log('PASS immediate first subtitle and manual choice survives delayed startup selection '+version);
}
(async()=>{for(const version of ['4.9.5.0','4.10.0.40'])await checkStartupSelection(version);})().catch(e=>{console.error(e);process.exitCode=1;});

// The capability marker is emitted only by the patched HTML video player,
// independently of browser brands and without mutating a shared profile object.
async function checkProfileMarker(version) {
 const src=fs.readFileSync(require('node:path').join(process.argv[2] || '.local/subtitle-implementation','web-'+version+'.js'),'utf8');
 const at=src.indexOf('function strmBridgeHlsCapable('),end=src.indexOf('function strmBridgeCandidate(',at);
 assert.ok(at>=0&&end>at);
 for(const [capable,hostHls,chromecast] of [[true,true,false],[false,true,false],[true,false,false],[true,true,true]]) {
   const original={Name:'Browser profile',CodecProfiles:[{Codec:'h264'}]};
   const scope={window:capable?{Worker:1,ReadableStream:1,AbortController:1,TextDecoder:1}:{},HtmlVideoPlayer:function(){},
     _browser:{default:{chromecast}},_htmlmediahelper:{default:{enableHlsJsPlayer:(runtime,type)=>{assert.equal(runtime,null);assert.equal(type,'Video');return hostHls;}}}};
   scope.HtmlVideoPlayer.prototype.getDeviceProfile=async()=>original;
   vm.runInNewContext(src.slice(at,end),scope);
   const start=src.indexOf('function HtmlVideoPlayer('),stop=src.indexOf('function onPictureInPictureError(',start);
   scope.resetVideoRendererSize=()=>{};
   scope._basehtmlplayer={default:function(){this.getDeviceProfile=async()=>original;}};
   vm.runInNewContext(src.slice(start,stop),scope);
   const result=await new scope.HtmlVideoPlayer().getDeviceProfile();
   assert.equal(result.Name,capable&&hostHls&&!chromecast?'STRM Bridge Web subtitles v2':'Browser profile');
   assert.equal(original.Name,'Browser profile');
   assert.equal(result.CodecProfiles,original.CodecProfiles);
 }
 console.log('PASS Web capability negotiation marker and unsupported-engine guard '+version);
}
(async()=>{for(const v of ['4.9.5.0','4.10.0.40'])await checkProfileMarker(v);})().catch(e=>{console.error(e);process.exitCode=1;});

// External ASS keeps the host's loader; only its observable HLS clock is bound.
async function checkExternalClockDispatch(version) {
 const src=fs.readFileSync(require('node:path').join(process.argv[2] || '.local/subtitle-implementation','web-'+version+'.js'),'utf8');
 const start=src.indexOf('function renderWithSubtitlesOctopus('),end=src.indexOf('function renderWithSubtitlesOctopusNative(',start);
 assert.ok(start>=0&&end>start);
 let native=0,clocks=0,imports=0;const renderFlags=[];
 const scope={URL,document:{baseURI:'http://localhost/web/index.html'},Emby:{importModule:async()=>{imports++;return{bindExternalClock:()=>clocks++};}},
   renderWithSubtitlesOctopusNative:async(...args)=>{native++;renderFlags.push(args.at(-1));},_connectionmanager:{default:{getApiClient:()=>({})}}};
 vm.createContext(scope);vm.runInContext(src.slice(start,end),scope);
 const instance={_hlsPlayer:{},_strmBridgeEpoch:1,_currentPlayOptions:{url:'http://localhost/master.m3u8?SegmentContainer=ts'}};
 const track={IsExternal:true,Codec:'ass'},media={Container:'mkv',Protocol:'Http'};
 await scope.renderWithSubtitlesOctopus(instance,{},track,{},media);assert.equal(native,1);assert.equal(clocks,1);assert.equal(renderFlags[0],true);
 for(const [player,t,m] of [[instance,{...track,IsExternal:false},media],[{},track,media],[instance,track,{...media,Protocol:'File'}],[instance,{...track,Codec:'srt'},media]])
   await scope.renderWithSubtitlesOctopus(player,{},t,{},m);
 assert.equal(native,5);assert.equal(clocks,1);assert.equal(imports,1);assert.ok(renderFlags.slice(1).every(v=>v===undefined));
 let release;scope.Emby.importModule=()=>new Promise(r=>{release=r;});
 const delayed=scope.renderWithSubtitlesOctopus(instance,{},track,{},media);instance._strmBridgeEpoch++;
 release({bindExternalClock:()=>clocks++});await delayed;assert.equal(native,5);assert.equal(clocks,1);
 console.log('PASS external ASS host loading, clock-only dispatch, scope and stale-import exclusion '+version);
}
(async()=>{for(const v of ['4.9.5.0','4.10.0.40'])await checkExternalClockDispatch(v);})().catch(e=>{console.error(e);process.exitCode=1;});

async function checkExternalLoaderEpoch(version) {
 const src=fs.readFileSync(require('node:path').join(process.argv[2] || '.local/subtitle-implementation','web-'+version+'.js'),'utf8');
 const begin=src.indexOf('function renderWithSubtitlesOctopusNative('),end=src.indexOf('function renderAssSsa(',begin);
 let release,constructed=0;
 const scope={window:{},_browser:{default:{}},fetch:()=>{},getTextTrackUrl:()=>'/fixture.ass',loadWebVTT:async()=>{},
   fetchSubtitleContent:async()=>'',getFallbackFontUrl:async()=>'/fixture.woff2',
   Emby:{importModule:()=>new Promise(resolve=>{release=()=>resolve(function(){constructed++;});})}};
 vm.createContext(scope);vm.runInContext(src.slice(begin,end),scope);
 const instance={_strmBridgeEpoch:1};
 const pending=scope.renderWithSubtitlesOctopusNative(instance,{},{},{},{});
 assert.equal(typeof pending?.then,'function','Both host loaders must expose renderer completion.');
 await new Promise(resolve=>setImmediate(resolve));instance._strmBridgeEpoch++;
 release();await pending;assert.equal(constructed,0);
 console.log('PASS stale native ASS content/font response cannot recreate a closed track '+version);
}
(async()=>{for(const v of ['4.9.5.0','4.10.0.40'])await checkExternalLoaderEpoch(v);})().catch(e=>{console.error(e);process.exitCode=1;});

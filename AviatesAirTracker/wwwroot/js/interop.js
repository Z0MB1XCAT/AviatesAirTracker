// Aviates Air Tracker — JS Interop helpers

window.aviatesMap = {
    _map: null,
    _aircraftMarker: null,
    _pathLine: null,
    _plannedLine: null,
    _airportMarkers: [],
    _waypointDots: [],

    init: function (lat, lon) {
        // Always destroy and recreate — handles page re-navigation cleanly
        if (this._map) {
            this._map.remove();
            this._map = null;
            this._aircraftMarker = null;
            this._pathLine = null;
            this._plannedLine = null;
            this._airportMarkers = [];
            this._waypointDots = [];
        }

        this._map = L.map('leaflet-map', {
            center: [lat || 51.5, lon || -0.1],
            zoom: 7,
            minZoom: 3,
            maxZoom: 18,
            maxBounds: [[-85.06, -180], [85.06, 180]],
            maxBoundsViscosity: 1.0,
            zoomControl: true,
            attributionControl: true
        });

        L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', {
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> &copy; <a href="https://carto.com/">CARTO</a>',
            subdomains: 'abcd',
            maxZoom: 19
        }).addTo(this._map);

        // SVG aircraft marker — rotates around its centre point
        var planeSvg =
            '<svg viewBox="0 0 24 24" width="30" height="30" ' +
            'style="fill:#3B82F6;filter:drop-shadow(0 0 6px rgba(59,130,246,0.9));' +
            'transform-origin:center center;display:block;">' +
            '<path d="M21 16v-2l-8-5V3.5a1.5 1.5 0 00-3 0V9l-8 5v2l8-2.5V19l-2 1.5V22l3.5-1 3.5 1v-1.5L13 19v-5.5l8 2.5z"/>' +
            '</svg>';

        var planeIcon = L.divIcon({
            html: planeSvg,
            className: '',
            iconSize: [30, 30],
            iconAnchor: [15, 15]
        });

        this._aircraftMarker = L.marker([lat || 51.5, lon || -0.1], {
            icon: planeIcon,
            zIndexOffset: 1000
        }).addTo(this._map);

        // Flown path — solid accent blue
        this._pathLine = L.polyline([], {
            color: '#3B82F6',
            weight: 2.5,
            opacity: 0.9
        }).addTo(this._map);

        // Planned route — dashed lighter blue
        this._plannedLine = L.polyline([], {
            color: '#93C5FD',
            weight: 2,
            opacity: 0.6,
            dashArray: '8 5'
        }).addTo(this._map);

        // Let the CSS grid finish laying out before Leaflet measures the container
        var m = this._map;
        requestAnimationFrame(function () { if (m) m.invalidateSize(); });
    },

    updatePosition: function (lat, lon, headingDeg) {
        if (!this._map || !this._aircraftMarker) return;
        var pos = [lat, lon];
        this._aircraftMarker.setLatLng(pos);

        // Rotate the SVG to match aircraft heading
        var el = this._aircraftMarker.getElement();
        if (el) {
            var svg = el.querySelector('svg');
            if (svg) svg.style.transform = 'rotate(' + (headingDeg || 0) + 'deg)';
        }

        // Append to flown-path trail (cap at 2000 points ≈ 100 min at 4 Hz)
        var path = this._pathLine.getLatLngs();
        path.push(pos);
        if (path.length > 2000) path.shift();
        this._pathLine.setLatLngs(path);
    },

    setPlannedRoute: function (points) {
        if (!this._map) return;

        // Remove old waypoint markers
        this._waypointDots.forEach(function (d) { d.remove(); });
        this._waypointDots = [];

        if (!points || points.length === 0) {
            this._plannedLine.setLatLngs([]);
            return;
        }

        // Drop any stray 0,0 points that made it through (safety net)
        var valid = points.filter(function (p) { return p.lat !== 0 || p.lon !== 0; });
        if (valid.length === 0) { this._plannedLine.setLatLngs([]); return; }

        var latlngs = valid.map(function (p) { return [p.lat, p.lon]; });
        this._plannedLine.setLatLngs(latlngs);

        // Named waypoint labels — show every named fix (not ltlg lat/lon points)
        // Thin out on very long routes to prevent clutter (max ~80 labels)
        var named = valid.filter(function (p) { return p.type !== 'ltlg' && p.id; });
        var step  = Math.max(1, Math.floor(named.length / 80));
        for (var i = 0; i < named.length; i += step) {
            var p   = named[i];
            var ll  = [p.lat, p.lon];
            // Small diamond dot
            var dot = L.circleMarker(ll, {
                radius: 3,
                color: '#60A5FA',
                fillColor: '#1E3A5F',
                fillOpacity: 1,
                weight: 1.5
            }).addTo(this._map);
            // Tooltip shows the fix identifier on hover
            dot.bindTooltip(p.id, {
                permanent: false,
                direction: 'top',
                offset: [0, -4],
                className: 'avt-wp-tooltip'
            });
            this._waypointDots.push(dot);
        }
    },

    addAirportMarker: function (lat, lon, icao, markerType) {
        if (!this._map) return;
        // markerType: "dep" = green, "arr" = amber, "alt" = violet
        var colors = { dep: '#10B981', arr: '#F59E0B', alt: '#A855F7' };
        var color = colors[markerType] || (markerType === true ? '#10B981' : '#F59E0B');
        var prefix = markerType === 'alt' ? 'ALT ' : '';
        var marker = L.marker([lat, lon], {
            icon: L.divIcon({
                html: '<div style="background:' + color + ';color:#fff;font-size:10px;font-weight:700;' +
                      'padding:3px 7px;border-radius:5px;white-space:nowrap;font-family:monospace;' +
                      'box-shadow:0 2px 8px rgba(0,0,0,0.5);border:1px solid rgba(255,255,255,0.2);">' +
                      prefix + icao + '</div>',
                className: '',
                iconAnchor: [22, 10]
            }),
            zIndexOffset: 500
        }).addTo(this._map);
        this._airportMarkers.push(marker);
    },

    clearAirportMarkers: function () {
        this._airportMarkers.forEach(function (m) { m.remove(); });
        this._airportMarkers = [];
    },

    fitRouteBounds: function () {
        if (!this._map) return;
        var allPoints = [];
        var routePts = this._plannedLine.getLatLngs();
        if (routePts && routePts.length > 0) allPoints = allPoints.concat(routePts);
        this._airportMarkers.forEach(function (m) { allPoints.push(m.getLatLng()); });
        if (allPoints.length > 1) {
            this._map.fitBounds(L.latLngBounds(allPoints), { padding: [48, 48], maxZoom: 10 });
        }
    },

    resetPath: function () {
        if (!this._map || !this._pathLine) return;
        this._pathLine.setLatLngs([]);
    },

    setFlightPath: function (points) {
        if (!this._map || !this._pathLine) return;
        this._pathLine.setLatLngs(points.map(function (p) { return [p.lat, p.lon]; }));
    },

    panTo: function (lat, lon) {
        if (!this._map) return;
        this._map.panTo([lat, lon], { animate: true, duration: 0.5 });
    },

    invalidateSize: function () {
        if (!this._map) return;
        setTimeout(function () {
            if (window.aviatesMap._map) window.aviatesMap._map.invalidateSize();
        }, 100);
    },

    destroy: function () {
        if (this._map) {
            this._map.remove();
            this._map = null;
            this._aircraftMarker = null;
            this._pathLine = null;
            this._plannedLine = null;
            this._airportMarkers = [];
            this._waypointDots = [];
        }
    }
};

window.aviatesInterop = {
    scrollToTop: function (elementId) {
        var el = document.getElementById(elementId);
        if (el) el.scrollTop = 0;
    }
};

// ── Theme management ────────────────────────────────────────
window.aviatesTheme = {
    _mql: null,
    _mqlListener: null,

    // Apply a theme and persist it. theme = "system" | "light" | "dark"
    apply: function (theme) {
        try { localStorage.setItem('avt_theme', theme); } catch (e) {}
        var dark = theme === 'dark' ||
            (theme === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
        document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
    },

    // Watch for OS-level theme changes and re-apply when in "system" mode
    watchSystem: function () {
        var self = this;
        if (this._mql && this._mqlListener) {
            this._mql.removeEventListener('change', this._mqlListener);
        }
        this._mql = window.matchMedia('(prefers-color-scheme: dark)');
        this._mqlListener = function (e) {
            var saved = 'system';
            try { saved = localStorage.getItem('avt_theme') || 'system'; } catch (ex) {}
            if (saved === 'system') {
                document.documentElement.setAttribute('data-theme', e.matches ? 'dark' : 'light');
            }
        };
        this._mql.addEventListener('change', this._mqlListener);
    },

    stopWatching: function () {
        if (this._mql && this._mqlListener) {
            this._mql.removeEventListener('change', this._mqlListener);
            this._mql = null;
            this._mqlListener = null;
        }
    }
};

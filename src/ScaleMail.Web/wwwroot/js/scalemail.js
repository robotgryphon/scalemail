// Browser-side helpers for the Blazor app: image decoding, file download, SVG hit-testing, 3D preview.
window.scalemail = (() => {
    let decoded = null;

    async function decodeImage(bytes, mime) {
        const bmp = await createImageBitmap(new Blob([bytes], { type: mime || 'image/png' }));
        const canvas = document.createElement('canvas');
        canvas.width = bmp.width; canvas.height = bmp.height;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(bmp, 0, 0);
        decoded = ctx.getImageData(0, 0, bmp.width, bmp.height).data;
        return [bmp.width, bmp.height];
    }
    function takePixels() { const d = decoded; decoded = null; return new Uint8Array(d.buffer, d.byteOffset, d.byteLength); }

    function download(name, bytes, mime) {
        const url = URL.createObjectURL(new Blob([bytes], { type: mime || 'application/octet-stream' }));
        const a = document.createElement('a');
        a.href = url; a.download = name; document.body.appendChild(a); a.click(); a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    function svgPoint(svg, clientX, clientY) {
        const p = svg.createSVGPoint(); p.x = clientX; p.y = clientY;
        const q = p.matrixTransform(svg.getScreenCTM().inverse());
        return [q.x, q.y];
    }

    // ---- 3D preview: WebGL with a depth buffer; drag to orbit, wheel to zoom ----
    const view = { yaw: 0.5, pitch: 0.2, zoom: 1, canvas: null, gl: null, prog: null, buf: null, count: 0, center: [0, 0, 0], radius: 1, pending: false };

    const VS = `attribute vec3 pos; attribute vec3 nrm; attribute vec3 col; uniform mat4 mvp; uniform mat3 rot;
        varying vec3 vn; varying vec3 vc; void main(){ vn = rot * nrm; vc = col; gl_Position = mvp * vec4(pos,1.0); }`;
    const FS = `precision mediump float; varying vec3 vn; varying vec3 vc;
        void main(){ vec3 n = normalize(vn); if(!gl_FrontFacing) n = -n;
          float l = 0.35 + 0.45*max(dot(n, normalize(vec3(0.3,0.5,0.8))),0.0) + 0.2*max(dot(n, normalize(vec3(-0.6,-0.2,0.4))),0.0);
          gl_FragColor = vec4(vc * l, 1.0); }`;

    function compile(gl, type, src) {
        const sh = gl.createShader(type); gl.shaderSource(sh, src); gl.compileShader(sh);
        if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(sh));
        return sh;
    }

    function initView(canvas) {
        view.canvas = canvas;
        const gl = canvas.getContext('webgl', { antialias: true });
        if (!gl) throw new Error('WebGL is not available in this browser.');
        const prog = gl.createProgram();
        gl.attachShader(prog, compile(gl, gl.VERTEX_SHADER, VS));
        gl.attachShader(prog, compile(gl, gl.FRAGMENT_SHADER, FS));
        gl.linkProgram(prog);
        view.gl = gl; view.prog = prog; view.buf = gl.createBuffer();
        let drag = null;
        canvas.onpointerdown = e => { drag = [e.clientX, e.clientY]; canvas.setPointerCapture(e.pointerId); };
        canvas.onpointerup = () => { drag = null; };
        canvas.onpointermove = e => {
            if (!drag) return;
            view.yaw += (e.clientX - drag[0]) * 0.01;
            view.pitch = Math.max(-1.55, Math.min(1.55, view.pitch + (e.clientY - drag[1]) * 0.01));
            drag = [e.clientX, e.clientY]; schedule();
        };
        canvas.onwheel = e => { e.preventDefault(); view.zoom *= Math.exp(-e.deltaY * 0.001); schedule(); };
        new ResizeObserver(schedule).observe(canvas);
    }

    function setMesh(canvas, triBytes, colored) {
        if (view.canvas !== canvas) initView(canvas);
        const t = new Float32Array(triBytes.slice().buffer);
        const n = t.length / 9;
        // interleaved per vertex: pos(3) nrm(3) col(3)
        const data = new Float32Array(n * 3 * 9);
        let min = [1e9, 1e9, 1e9], max = [-1e9, -1e9, -1e9];
        for (let i = 0; i < n; i++) {
            const o = i * 9;
            const ax = t[o + 3] - t[o], ay = t[o + 4] - t[o + 1], az = t[o + 5] - t[o + 2];
            const bx = t[o + 6] - t[o], by = t[o + 7] - t[o + 1], bz = t[o + 8] - t[o + 2];
            let nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
            const l = Math.hypot(nx, ny, nz) || 1; nx /= l; ny /= l; nz /= l;
            const c = colored[i] === 2 ? [0.95, 0.72, 0.2] : colored[i] ? [0.47, 0.5, 0.62] : [0.9, 0.9, 0.92];
            for (let k = 0; k < 3; k++) {
                const d = (i * 3 + k) * 9;
                for (let j = 0; j < 3; j++) {
                    const v = t[o + k * 3 + j];
                    data[d + j] = v; data[d + 3 + j] = j === 0 ? nx : j === 1 ? ny : nz; data[d + 6 + j] = c[j];
                    if (v < min[j]) min[j] = v; if (v > max[j]) max[j] = v;
                }
            }
        }
        view.center = min.map((m, k) => (m + max[k]) / 2);
        view.radius = Math.hypot(max[0] - min[0], max[1] - min[1], max[2] - min[2]) / 2 || 1;
        const gl = view.gl;
        gl.bindBuffer(gl.ARRAY_BUFFER, view.buf);
        gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
        view.count = n * 3;
        schedule();
    }

    function schedule() {
        if (view.pending) return;
        view.pending = true;
        requestAnimationFrame(() => { view.pending = false; draw(); });
    }

    function mul(a, b) { // column-major 4x4
        const r = new Float32Array(16);
        for (let c = 0; c < 4; c++) for (let rr = 0; rr < 4; rr++)
            r[c * 4 + rr] = a[rr] * b[c * 4] + a[4 + rr] * b[c * 4 + 1] + a[8 + rr] * b[c * 4 + 2] + a[12 + rr] * b[c * 4 + 3];
        return r;
    }

    function draw() {
        const c = view.canvas, gl = view.gl; if (!c || !gl || !view.count) return;
        const dpr = window.devicePixelRatio || 1;
        const w = Math.max(1, Math.floor(c.clientWidth * dpr)), h = Math.max(1, Math.floor(c.clientHeight * dpr));
        if (c.width !== w || c.height !== h) { c.width = w; c.height = h; }
        gl.viewport(0, 0, w, h);
        gl.clearColor(0.06, 0.07, 0.08, 1); gl.enable(gl.DEPTH_TEST); gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
        const cy = Math.cos(view.yaw), sy = Math.sin(view.yaw), cp = Math.cos(view.pitch), sp = Math.sin(view.pitch);
        // rotation: yaw about Y, then pitch about X (column-major)
        const ry = [cy, 0, -sy, 0, 0, 1, 0, 0, sy, 0, cy, 0, 0, 0, 0, 1];
        const rx = [1, 0, 0, 0, 0, cp, sp, 0, 0, -sp, cp, 0, 0, 0, 0, 1];
        const rot = mul(rx, ry);
        const dist = view.radius * 3 / view.zoom, f = 1 / Math.tan(0.3), aspect = w / h;
        const near = Math.max(0.1, dist - view.radius * 2), far = dist + view.radius * 2;
        const proj = [f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0];
        const cen = view.center;
        const tr = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -cen[0], -cen[1], -cen[2] - 0, 1];
        const back = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, -dist, 1];
        const mvp = mul(proj, mul(back, mul(rot, tr)));
        const rot3 = new Float32Array([rot[0], rot[1], rot[2], rot[4], rot[5], rot[6], rot[8], rot[9], rot[10]]);
        gl.useProgram(view.prog);
        gl.uniformMatrix4fv(gl.getUniformLocation(view.prog, 'mvp'), false, mvp);
        gl.uniformMatrix3fv(gl.getUniformLocation(view.prog, 'rot'), false, rot3);
        gl.bindBuffer(gl.ARRAY_BUFFER, view.buf);
        [['pos', 0], ['nrm', 3], ['col', 6]].forEach(([name, off]) => {
            const loc = gl.getAttribLocation(view.prog, name);
            gl.enableVertexAttribArray(loc); gl.vertexAttribPointer(loc, 3, gl.FLOAT, false, 36, off * 4);
        });
        gl.drawArrays(gl.TRIANGLES, 0, view.count);
    }

    // Reports the stage size to .NET whenever it changes.
    function observe(el, ref) {
        const report = () => ref.invokeMethodAsync('OnStageSize', el.clientWidth, el.clientHeight);
        new ResizeObserver(report).observe(el);
        report();
    }

    // Firefox only starts a drag when data is set; toolbox items carry their kind in data-drag.
    document.addEventListener('dragstart', e => {
        const t = e.target.closest && e.target.closest('[data-drag]');
        if (t) { e.dataTransfer.setData('text/plain', t.dataset.drag); e.dataTransfer.effectAllowed = 'copy'; }
    });

    function setCamera(yaw, pitch) {
        view.yaw = yaw; view.pitch = pitch; view.zoom = 1;
        schedule();
    }

    return { setCamera, decodeImage, takePixels, download, svgPoint, setMesh, observe };
})();

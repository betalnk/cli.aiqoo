class CliPcmCapture extends AudioWorkletProcessor {
  constructor() {
    super();
    this.samples = new Float32Array(4096);
    this.length = 0;
    this.port.onmessage = event => {
      if (event.data?.type !== 'flush') return;
      this.flush();
      this.port.postMessage({ type: 'flushed' });
    };
  }

  flush() {
    if (!this.length) return;
    const buffer = this.samples.slice(0, this.length).buffer;
    this.port.postMessage({ type: 'chunk', buffer }, [buffer]);
    this.length = 0;
  }

  process(inputs) {
    const channel = inputs[0]?.[0];
    if (!channel) return true;
    let offset = 0;
    while (offset < channel.length) {
      const count = Math.min(this.samples.length - this.length, channel.length - offset);
      this.samples.set(channel.subarray(offset, offset + count), this.length);
      this.length += count;
      offset += count;
      if (this.length === this.samples.length) this.flush();
    }
    return true;
  }
}

registerProcessor('cli-pcm-capture', CliPcmCapture);

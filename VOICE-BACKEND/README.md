# Dispatch Radio Backend — Oracle Setup

## 1. Create the Oracle Cloud account and VM

1. Sign up for an Oracle Cloud Free Tier account at oracle.com/cloud/free.
2. In the console, go to **Compute → Instances → Create Instance**.
3. Choose the **Always Free** shape: `VM.Standard.A1.Flex` (1 OCPU / 6GB is plenty) or `VM.Standard.E2.1.Micro`.
4. Image: **Canonical Ubuntu 22.04**.
5. Add your SSH public key under "Add SSH keys" (generate one locally with `ssh-keygen` if you don't have one).
6. Create the instance and note its **public IP address**.

## 2. Open the port

1. In the console, go to your VM's subnet → **Security Lists** → the default list → **Add Ingress Rule**.
   - Source CIDR: `0.0.0.0/0`
   - Destination port range: `8080`
   - Protocol: TCP
2. SSH into the VM: `ssh ubuntu@<your-vm-ip>`
3. Ubuntu's firewall is off by default on Oracle images, but if `ufw` is active: `sudo ufw allow 8080/tcp`

## 3. Install Node.js 20

```bash
curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
sudo apt-get install -y nodejs
node --version   # confirm v20.x
```

## 4. Deploy the backend

```bash
sudo useradd -r -s /usr/sbin/nologin aviates
sudo mkdir -p /opt/aviates-dispatch-backend
sudo chown ubuntu:ubuntu /opt/aviates-dispatch-backend
```

From your machine, copy the `VOICE-BACKEND/` folder contents to the VM (or `git clone` the repo there and `cd` into `VOICE-BACKEND/`):

```bash
scp -r VOICE-BACKEND/* ubuntu@<your-vm-ip>:/opt/aviates-dispatch-backend/
```

On the VM:

```bash
cd /opt/aviates-dispatch-backend
npm ci --omit=dev
sudo chown -R aviates:aviates /opt/aviates-dispatch-backend
```

## 5. Set the admin key and install the service

Edit the systemd unit to set a real admin key (this gates the `/admin` WebSocket feed — anyone with this key can see all tuned pilots' positions, so keep it secret):

```bash
sudo cp deploy/aviates-dispatch-backend.service /etc/systemd/system/
sudo nano /etc/systemd/system/aviates-dispatch-backend.service
# replace ADMIN_WS_KEY=CHANGE_ME with a long random value
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now aviates-dispatch-backend
sudo systemctl status aviates-dispatch-backend
```

## 6. Verify

```bash
curl http://localhost:8080/health
# from your own machine:
curl http://<your-vm-ip>:8080/health
```

Both should print `ok`. Once confirmed, set:
- `AviatesAirTracker` → Settings → Dispatch Backend URL → `ws://<your-vm-ip>:8080`
- `AviatesAirWebsite` → `.env.local` → `NEXT_PUBLIC_DISPATCH_ADMIN_WS_URL=ws://<your-vm-ip>:8080/admin` and `NEXT_PUBLIC_DISPATCH_ADMIN_WS_KEY=<the same admin key>`

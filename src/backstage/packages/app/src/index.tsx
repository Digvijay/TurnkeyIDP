import '@backstage/cli/asset-types';
import ReactDOM from 'react-dom/client';

if (typeof window !== 'undefined' && (!window.crypto || !window.crypto.randomUUID)) {
  if (!window.crypto) {
    (window as any).crypto = {} as any;
  }
  window.crypto.randomUUID = function () {
    return '10000000-1000-4000-8000-100000000000'.replace(/[018]/g, (c: any) =>
      (c ^ (crypto.getRandomValues(new Uint8Array(1))[0] & (15 >> (c / 4)))).toString(16),
    ) as any;
  };
}

import App from './App';
import '@backstage/ui/css/styles.css';

ReactDOM.createRoot(document.getElementById('root')!).render(App.createRoot());

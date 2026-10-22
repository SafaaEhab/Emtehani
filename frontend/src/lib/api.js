export const API = import.meta.env.VITE_API_URL || 'http://localhost:5000';

const errorMessages = {
  400: 'البيانات المدخلة غير صحيحة. راجع الحقول وحاول مرة أخرى.',
  401: 'انتهت صلاحية الجلسة أو بيانات الدخول غير صحيحة. سجّل الدخول مرة أخرى.',
  403: 'ليس لديك صلاحية لتنفيذ هذا الإجراء.',
  404: 'لم نعثر على العنصر المطلوب أو لم يعد متاحاً.',
  409: 'لا يمكن تنفيذ الإجراء في الحالة الحالية. حدّث الصفحة ثم حاول مرة أخرى.',
  429: 'طلبات كثيرة خلال وقت قصير. انتظر قليلاً ثم حاول مجدداً.',
  500: 'حدث خطأ داخلي. حاول مرة أخرى، وإذا استمرت المشكلة تواصل مع الدعم.',
};

export async function request(path, { token, ...opts } = {}) {
  const headers = { ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(opts.headers || {}) };
  if (opts.body && !(opts.body instanceof FormData)) headers['Content-Type'] = 'application/json';

  let res;
  try {
    res = await fetch(API + path, { ...opts, headers });
  } catch {
    throw new Error('تعذر الاتصال بالخادم. تحقق من اتصال الإنترنت ثم حاول مرة أخرى.');
  }

  if (!res.ok) {
    let message = errorMessages[res.status] || 'تعذر إكمال العملية. حاول مرة أخرى.';
    try {
      const payload = await res.json();
      if (typeof payload.message === 'string' && payload.message.trim()) message = payload.message;
      else if (typeof payload.title === 'string' && payload.title.trim()) message = payload.title;
    } catch {
      // Keep the status-based message.
    }
    const error = new Error(message);
    error.status = res.status;
    throw error;
  }

  if (res.headers.get('content-type')?.includes('text/csv')) return res.blob();
  if (res.status === 204) return null;
  return res.json();
}

export function getStoredAuth() {
  try {
    const token = localStorage.getItem('emtehani-token') || '';
    const user = JSON.parse(localStorage.getItem('emtehani-user') || 'null');
    return { token, user };
  } catch {
    return { token: '', user: null };
  }
}

export function storeAuth(token, user) {
  localStorage.setItem('emtehani-token', token);
  localStorage.setItem('emtehani-user', JSON.stringify(user));
}

export function clearAuth() {
  localStorage.removeItem('emtehani-token');
  localStorage.removeItem('emtehani-user');
}

import React, { useState } from "react";
import {
  ArrowUpRight, BriefcaseBusiness, CheckCircle2, Code2, ExternalLink, GitBranch, GraduationCap, Languages, Link2, Mail, MapPin, MessageCircle,
  Moon, Phone, Rocket, Sun, Workflow
} from "lucide-react";
import "./DeveloperPage.css";

const PROFILE = {
  name: "Ahmed Gbreel",
  role: "Full Stack Developer & Software Engineer",
  degree: "خريج كلية الحاسبات والذكاء الاصطناعي",
  university: "جامعة بنها",
  graduation: "2025",
  location: "القاهرة، مصر",
  experience: "سنتان من الخبرة",
  email: "ahmedgbreel47@gmail.com",
  phone: "01159862510",
  whatsapp: "201159862510",
  github: "https://github.com/AGbreel",
  linkedin: "https://www.linkedin.com/in/ahmed-gbreel-8465b0352/",
  skills: ["React", "JavaScript", "C#", "ASP.NET Core", "SQL Server", "REST APIs", "Clean Architecture", "Responsive UI"],
};

const PROJECTS = [
  {
    number: "01",
    title: "Exam Platform",
    category: "منصة تعليمية",
    description: "منصة لإدارة الاختبارات، إنشاء الأسئلة، تنظيم المحاولات ومتابعة النتائج في تجربة استخدام واضحة.",
    tags: ["React", "ASP.NET Core", "SQL Server"],
  },
  {
    number: "02",
    title: "E-commerce",
    category: "تجارة إلكترونية",
    description: "متجر إلكتروني قابل للتوسع لعرض المنتجات وتنظيم تجربة التسوق وإدارة الوظائف الأساسية للمتجر.",
    tags: ["React", "REST APIs", "SQL Server"],
  },
  {
    number: "03",
    title: "Educational Center ERP",
    category: "إدارة مركز تعليمي",
    description: "نظام لإدارة عمليات مركز تعليمي، مع قابلية تطوير وحدات تناسب احتياجات العمل.",
    tags: ["ASP.NET Core", "C#", "SQL Server"],
  },
  {
    number: "04",
    title: "Portfolio",
    category: "موقع شخصي",
    description: "موقع شخصي يعرض المهارات والمشاريع والخدمات ووسائل التواصل بشكل احترافي ومتجاوب.",
    tags: ["React", "Responsive Design", "UI/UX"],
  },
];

const SERVICES = [
  {
    icon: Code2,
    title: "تطوير مواقع الويب",
    description: "مواقع حديثة وسريعة ومتجاوبة مع الموبايل والكمبيوتر، بتجربة استخدام واضحة.",
  },
  {
    icon: Workflow,
    title: "أنظمة إدارية مخصصة",
    description: "لوحات تحكم وأنظمة لإدارة العمليات والبيانات بما يناسب طبيعة مشروعك.",
  },
  {
    icon: BriefcaseBusiness,
    title: "متاجر إلكترونية",
    description: "بناء متاجر قابلة للتوسع وتنظيم المنتجات والواجهات وتكاملات الـ API حسب المتطلبات.",
  },
  {
    icon: Rocket,
    title: "Back-end وواجهات API",
    description: "تطوير خدمات خلفية منظمة باستخدام ASP.NET Core وSQL Server مع الاهتمام بقابلية الصيانة.",
  },
];

export default function DeveloperPage({ onBack }) {
  const [language, setLanguage] = useState("ar");
  const [theme, setTheme] = useState(() => {
    if (typeof document !== "undefined" && document.documentElement.dataset.theme) {
      return document.documentElement.dataset.theme;
    }
    return "dark";
  });
  const isArabic = language === "ar";

  const copy = isArabic ? {
    navProjects: "المشاريع", navServices: "الخدمات", navAbout: "نبذة عني", navContact: "تواصل معي",
    eyebrow: "أحوّل الأفكار إلى منتجات رقمية",
    headlineA: "عندك فكرة؟",
    headlineB: "خلّينا نبنيها صح.",
    intro: "أنا أحمد جبرائيل، مطور Full Stack ومهندس برمجيات. أساعد الأفراد والشركات على تحويل أفكارهم إلى مواقع وتطبيقات عملية، بتصميم مميز وكود منظم قابل للتطوير.",
    primaryCta: "ناقش مشروعك معي", secondaryCta: "استكشف أعمالي",
    experience: "خبرة عملية", projects: "نماذج من المشاريع", technologies: "تقنيات أعمل بها",
    aboutEyebrow: "نبذة عني", aboutTitle: "شريكك التقني من الفكرة إلى الإطلاق",
    aboutText: "خريج كلية الحاسبات والذكاء الاصطناعي بجامعة بنها عام 2025، ولدي خبرة لمدة سنتين في تطوير البرمجيات. أركز على فهم احتياج المشروع أولاً، ثم بناء حل واضح وعملي يمكن تطويره وصيانته مع نمو العمل.",
    degree: "المؤهل", graduation: "سنة التخرج", location: "الموقع", english: "الإنجليزية",
    intermediate: "متوسط", projectsEyebrow: "أعمال مختارة", projectsTitle: "مشاريع تعكس ما أستطيع بناءه",
    projectsText: "نماذج لمجالات أعمل عليها. تفاصيل كل مشروع وروابط العرض يمكن إضافتها عند تجهيز النسخ المنشورة.",
    servicesEyebrow: "كيف أساعدك؟", servicesTitle: "حلول برمجية حسب احتياجك",
    servicesText: "سواء كنت تبدأ من الصفر أو تحتاج إلى تطوير نظام موجود، يمكننا تحديد النطاق المناسب والعمل عليه خطوة بخطوة.",
    processEyebrow: "طريقة العمل", processTitle: "خطوات واضحة من البداية",
    steps: [
      ["01", "نفهم الفكرة", "نحدد أهداف المشروع والمستخدمين والوظائف المطلوبة."],
      ["02", "نخطط ونصمم", "نرتب الأولويات ونحدد تجربة الاستخدام والبنية التقنية."],
      ["03", "نطوّر ونختبر", "نبني الحل على مراحل ونراجع الوظائف قبل التسليم."],
      ["04", "نسلّم وندعم", "نسلّم المشروع مع توضيح طريقة التشغيل والخطوات التالية."],
    ],
    contactEyebrow: "لديك مشروع؟", contactTitle: "خلّينا نتكلم عن فكرتك",
    contactText: "ابعت تفاصيل مشروعك، وسنبدأ بتحديد المطلوب والخيارات المناسبة للتنفيذ.",
    email: "راسلني عبر البريد", whatsapp: "تواصل عبر واتساب", github: "شاهد GitHub",
    linkedin: "تواصل على LinkedIn", formName: "اسمك", formEmail: "بريدك الإلكتروني",
    formType: "نوع المشروع", formBudget: "الميزانية التقريبية (اختياري)",
    formDetails: "احكي لي عن مشروعك", formSubmit: "إرسال الطلب عبر واتساب",
    namePlaceholder: "اكتب اسمك", emailPlaceholder: "name@example.com",
    detailsPlaceholder: "ما الفكرة؟ وما أهم الوظائف التي تحتاجها؟",
    selectProject: "اختر نوع المشروع", website: "موقع تعريفي", ecommerce: "متجر إلكتروني",
    admin: "نظام إداري / ERP", education: "منصة تعليمية", api: "Back-end / API",
    other: "مشروع آخر", budgetPlaceholder: "مثال: قابل للنقاش",
    footer: "صُنع باهتمام بواسطة Ahmed Gbreel", top: "العودة للأعلى",
    skillTitle: "التقنيات والأدوات", skillText: "أختار الأدوات وفق متطلبات المشروع، مع الاهتمام بالأداء والتنظيم وسهولة الصيانة.",
  } : {
    navProjects: "Projects", navServices: "Services", navAbout: "About", navContact: "Contact",
    eyebrow: "Turning ideas into digital products",
    headlineA: "Have an idea?",
    headlineB: "Let's build it right.",
    intro: "I'm Ahmed Gbreel, a Full Stack Developer and Software Engineer. I help individuals and businesses turn ideas into practical websites and applications with thoughtful design and maintainable code.",
    primaryCta: "Discuss your project", secondaryCta: "Explore my work",
    experience: "Years of experience", projects: "Project examples", technologies: "Technologies I use",
    aboutEyebrow: "About me", aboutTitle: "Your technical partner from idea to launch",
    aboutText: "I graduated from the Faculty of Computers and Artificial Intelligence at Benha University in 2025 and have two years of software development experience. I focus on understanding the business need first, then building practical solutions that can evolve as the project grows.",
    degree: "Degree", graduation: "Graduation", location: "Location", english: "English",
    intermediate: "Intermediate", projectsEyebrow: "Selected work", projectsTitle: "Projects that show what I can build",
    projectsText: "Examples of the areas I work in. Live demos and repository links can be added when published versions are available.",
    servicesEyebrow: "What I do", servicesTitle: "Software solutions for your needs",
    servicesText: "Whether you're starting from scratch or improving an existing system, we can define the right scope and build it step by step.",
    processEyebrow: "My process", processTitle: "A clear process from day one",
    steps: [
      ["01", "Understand", "Define goals, users, and the features your project needs."],
      ["02", "Plan & design", "Prioritize features and map the user experience and architecture."],
      ["03", "Build & test", "Develop in stages and validate functionality before delivery."],
      ["04", "Deliver & support", "Hand over the project with setup guidance and next steps."],
    ],
    contactEyebrow: "Have a project?", contactTitle: "Let's talk about your idea",
    contactText: "Share a few details about your project and we'll discuss the scope and a suitable approach.",
    email: "Email me", whatsapp: "Chat on WhatsApp", github: "View GitHub",
    linkedin: "Connect on LinkedIn", formName: "Your name", formEmail: "Your email",
    formType: "Project type", formBudget: "Estimated budget (optional)",
    formDetails: "Tell me about your project", formSubmit: "Send request via WhatsApp",
    namePlaceholder: "Your name", emailPlaceholder: "name@example.com",
    detailsPlaceholder: "What's the idea, and what features do you need?",
    selectProject: "Choose project type", website: "Business website", ecommerce: "E-commerce store",
    admin: "Admin system / ERP", education: "Educational platform", api: "Back-end / API",
    other: "Other project", budgetPlaceholder: "e.g. Open to discussion",
    footer: "Crafted with care by Ahmed Gbreel", top: "Back to top",
    skillTitle: "Tech stack", skillText: "I choose tools based on project requirements, with a focus on performance, structure, and maintainability.",
  };

  function submitProject(event) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const fields = [
      `Name: ${data.get("name") || "-"}`,
      `Email: ${data.get("email") || "-"}`,
      `Project type: ${data.get("type") || "-"}`,
      `Estimated budget: ${data.get("budget") || "Not specified"}`,
      `Project details: ${data.get("details") || "-"}`,
    ];
    const message = `Hello Ahmed, I'd like to discuss a project:\n${fields.join("\n")}`;
    window.open(`https://wa.me/${PROFILE.whatsapp}?text=${encodeURIComponent(message)}`, "_blank", "noopener,noreferrer");
  }

  function toggleTheme() {
    const next = theme === "dark" ? "light" : "dark";
    setTheme(next);
    document.documentElement.dataset.theme = next;
    try { localStorage.setItem("emtehani-theme", next); } catch { }
  }

  return (
    <main className={`portfolio-page ${theme === "light" ? "portfolio-light" : "portfolio-dark"}`} dir={isArabic ? "rtl" : "ltr"}>
      <header className="portfolio-nav">
        <a className="portfolio-brand" href="#portfolio-home" aria-label="Ahmed Gbreel home">
          <span className="portfolio-brand-mark"><Code2 size={20} /></span>
          <span>Ahmed<span className="accent-text">.</span></span>
        </a>
        <nav className="portfolio-nav-links" aria-label="Main navigation">
          <a href="#portfolio-about">{copy.navAbout}</a>
          <a href="#portfolio-projects">{copy.navProjects}</a>
          <a href="#portfolio-services">{copy.navServices}</a>
          <a href="#portfolio-contact">{copy.navContact}</a>
        </nav>
        <div className="portfolio-nav-actions">
          <button type="button" className="portfolio-icon-button" onClick={() => setLanguage(language === "ar" ? "en" : "ar")} aria-label="Switch language">
            <Languages size={18} /><span>{language === "ar" ? "EN" : "عربي"}</span>
          </button>
          <button type="button" className="portfolio-icon-button" onClick={toggleTheme} aria-label="Toggle color theme">
            {theme === "dark" ? <Sun size={18} /> : <Moon size={18} />}
          </button>
          {onBack && <button type="button" className="portfolio-icon-button portfolio-back-button" onClick={onBack}>
            <ArrowUpRight size={18} /><span>{isArabic ? "العودة لمنصة امتحاني" : "Back to Emtehani"}</span>
          </button>}
        </div>
      </header>

      <section className="portfolio-hero" id="portfolio-home">
        <div className="portfolio-hero-glow portfolio-glow-one" />
        <div className="portfolio-hero-glow portfolio-glow-two" />
        <div className="portfolio-hero-content">
          <div className="portfolio-status"><span /> {copy.eyebrow}</div>
          <h1>{copy.headlineA}<br /><span>{copy.headlineB}</span></h1>
          <p className="portfolio-hero-description">{copy.intro}</p>
          <div className="portfolio-hero-actions">
            <a className="portfolio-button portfolio-button-primary" href="#portfolio-contact">{copy.primaryCta}<ArrowUpRight size={18} /></a>
            <a className="portfolio-button portfolio-button-secondary" href="#portfolio-projects">{copy.secondaryCta}</a>
          </div>
          <div className="portfolio-hero-meta">
            <span><MapPin size={16} /> {PROFILE.location}</span>
            <span><GraduationCap size={17} /> {PROFILE.university}</span>
          </div>
        </div>
        <div className="portfolio-hero-card" aria-label="Developer profile summary">
          <div className="portfolio-code-window">
            <div className="portfolio-window-top"><i /><i /><i /><span>developer.cs</span></div>
            <pre><code><span className="code-muted">public class</span> <span className="code-purple">Developer</span>{"\n"}{"{"}{"\n"}  <span className="code-muted">public string</span> Name = <span className="code-green">"Ahmed Gbreel"</span>;{"\n"}  <span className="code-muted">public string</span> Role = <span className="code-green">"Full Stack"</span>;{"\n"}  <span className="code-muted">public bool</span> BuildIdeas(){"\n"}    =&gt; <span className="code-purple">true</span>;{"\n"}{"}"}</code></pre>
          </div>
          <div className="portfolio-floating-chip"><CheckCircle2 size={17} /> Clean, maintainable code</div>
          <div className="portfolio-orbit" />
        </div>
      </section>

      <div className="portfolio-stats">
        <div><strong>2+</strong><span>{copy.experience}</span></div>
        <div><strong>4</strong><span>{copy.projects}</span></div>
        <div><strong>Full Stack</strong><span>{copy.technologies}</span></div>
      </div>

      <section className="portfolio-section portfolio-about" id="portfolio-about">
        <div className="portfolio-section-heading">
          <span className="portfolio-kicker">{copy.aboutEyebrow}</span>
          <h2>{copy.aboutTitle}</h2>
        </div>
        <div className="portfolio-about-grid">
          <p className="portfolio-about-text">{copy.aboutText}</p>
          <div className="portfolio-facts">
            <div><GraduationCap /><span>{copy.degree}</span><strong>{PROFILE.degree} — {PROFILE.university}</strong></div>
            <div><CheckCircle2 /><span>{copy.graduation}</span><strong>{PROFILE.graduation}</strong></div>
            <div><MapPin /><span>{copy.location}</span><strong>{PROFILE.location}</strong></div>
            <div><Languages /><span>{copy.english}</span><strong>{copy.intermediate}</strong></div>
          </div>
        </div>
        <div className="portfolio-skill-block">
          <div><h3>{copy.skillTitle}</h3><p>{copy.skillText}</p></div>
          <div className="portfolio-skills">{PROFILE.skills.map(skill => <span key={skill}>{skill}</span>)}</div>
        </div>
      </section>

      <section className="portfolio-section portfolio-projects-section" id="portfolio-projects">
        <div className="portfolio-section-heading">
          <span className="portfolio-kicker">{copy.projectsEyebrow}</span>
          <h2>{copy.projectsTitle}</h2>
          <p>{copy.projectsText}</p>
        </div>
        <div className="portfolio-project-grid">
          {PROJECTS.map(project => <article className="portfolio-project-card" key={project.number}>
            <div className="portfolio-project-top"><span>{project.number}</span><span className="portfolio-project-icon"><ArrowUpRight size={19} /></span></div>
            <span className="portfolio-project-category">{project.category}</span>
            <h3>{project.title}</h3>
            <p>{project.description}</p>
            <div className="portfolio-project-tags">{project.tags.map(tag => <span key={tag}>{tag}</span>)}</div>
          </article>)}
        </div>
      </section>

      <section className="portfolio-section" id="portfolio-services">
        <div className="portfolio-section-heading">
          <span className="portfolio-kicker">{copy.servicesEyebrow}</span>
          <h2>{copy.servicesTitle}</h2>
          <p>{copy.servicesText}</p>
        </div>
        <div className="portfolio-service-grid">
          {SERVICES.map(({ icon: Icon, title, description }) => <article className="portfolio-service-card" key={title}>
            <span className="portfolio-service-icon"><Icon size={23} /></span>
            <h3>{title}</h3><p>{description}</p>
          </article>)}
        </div>
      </section>

      <section className="portfolio-section portfolio-process-section">
        <div className="portfolio-section-heading">
          <span className="portfolio-kicker">{copy.processEyebrow}</span>
          <h2>{copy.processTitle}</h2>
        </div>
        <div className="portfolio-process-grid">
          {copy.steps.map(([number, title, description]) => <article key={number} className="portfolio-process-card">
            <span>{number}</span><h3>{title}</h3><p>{description}</p>
          </article>)}
        </div>
      </section>

      <section className="portfolio-contact-section" id="portfolio-contact">
        <div className="portfolio-contact-copy">
          <span className="portfolio-kicker">{copy.contactEyebrow}</span>
          <h2>{copy.contactTitle}</h2>
          <p>{copy.contactText}</p>
          <div className="portfolio-contact-links">
            <a href={`mailto:${PROFILE.email}`}><Mail size={18} /><span>{copy.email}<small>{PROFILE.email}</small></span><ExternalLink size={15} /></a>
            <a href={`https://wa.me/${PROFILE.whatsapp}`} target="_blank" rel="noreferrer"><MessageCircle size={18} /><span>{copy.whatsapp}<small>+{PROFILE.whatsapp}</small></span><ExternalLink size={15} /></a>
            <a href={PROFILE.github} target="_blank" rel="noreferrer"><GitBranch size={18} /><span>{copy.github}<small>github.com/AGbreel</small></span><ExternalLink size={15} /></a>
            <a href={PROFILE.linkedin} target="_blank" rel="noreferrer"><Link2 size={18} /><span>{copy.linkedin}<small>Ahmed Gbreel</small></span><ExternalLink size={15} /></a>
            <a href={`tel:${PROFILE.phone}`}><Phone size={18} /><span>{isArabic ? "اتصال هاتفي" : "Call me"}<small>{PROFILE.phone}</small></span><ExternalLink size={15} /></a>
          </div>
        </div>
        <form className="portfolio-project-form" onSubmit={submitProject}>
          <label>{copy.formName}<input name="name" required placeholder={copy.namePlaceholder} /></label>
          <label>{copy.formEmail}<input name="email" type="email" required placeholder={copy.emailPlaceholder} /></label>
          <label>{copy.formType}
            <select name="type" required defaultValue="">
              <option value="" disabled>{copy.selectProject}</option>
              <option>{copy.website}</option><option>{copy.ecommerce}</option><option>{copy.admin}</option>
              <option>{copy.education}</option><option>{copy.api}</option><option>{copy.other}</option>
            </select>
          </label>
          <label>{copy.formBudget}<input name="budget" placeholder={copy.budgetPlaceholder} /></label>
          <label className="portfolio-form-full">{copy.formDetails}<textarea name="details" rows={4} required placeholder={copy.detailsPlaceholder} /></label>
          <button className="portfolio-button portfolio-button-primary portfolio-form-full" type="submit">{copy.formSubmit}<ArrowUpRight size={18} /></button>
          <small className="portfolio-form-note">{isArabic ? "سيتم فتح واتساب برسالة مجهزة بالبيانات التي أدخلتها، ولن يتم تخزينها في هذا الموقع." : "WhatsApp will open with your details prefilled. This form does not store your data."}</small>
        </form>
      </section>

      <footer className="portfolio-footer">
        <a className="portfolio-brand" href="#portfolio-home"><span className="portfolio-brand-mark"><Code2 size={18} /></span><span>Ahmed<span className="accent-text">.</span></span></a>
        <p>© {new Date().getFullYear()} {copy.footer}</p>
        <a href="#portfolio-home">{copy.top} ↑</a>
      </footer>
    </main>
  );
}

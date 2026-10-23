import { themes as prismThemes } from 'prism-react-renderer';
import type { Config } from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const config: Config = {
  title: 'Mnemosyne.NET',
  tagline: 'Procedural memory for .NET agents: store, find and reuse the procedures that worked',

  // organizationName and projectName decide the deployment target; url and baseUrl decide the
  // absolute links the built site emits. All four have to agree with where the repository lives,
  // or the site builds clean and links off-site.
  url: 'https://marcelroozekrans.github.io',
  baseUrl: '/Mnemosyne.NET/',

  organizationName: 'MarcelRoozekrans',
  projectName: 'Mnemosyne.NET',

  // A broken link is a build failure. Never relax this to 'warn': a green build over broken links
  // is the defect the docs check exists to catch.
  onBrokenLinks: 'throw',

  markdown: {
    mermaid: true,
    hooks: {
      onBrokenMarkdownLinks: 'throw',
    },
  },

  themes: ['@docusaurus/theme-mermaid'],

  presets: [
    [
      'classic',
      {
        docs: {
          routeBasePath: '/',
          sidebarPath: './sidebars.ts',
          // Without this every page renders an "Edit this page" control, and a wrong URL here
          // 404s on every one of them while the build stays green.
          editUrl: 'https://github.com/MarcelRoozekrans/Mnemosyne.NET/edit/main/docs/',

          // docs/ also holds the project's working record: design specs and plans under
          // superpowers/, and the roadmap, state and phase backlogs under planning/. That is
          // internal state the project keeps against itself, not product documentation, so it
          // stays readable in the repository and out of the published site.
          exclude: ['superpowers/**', 'planning/**'],
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    navbar: {
      title: 'Mnemosyne.NET',
      items: [
        {
          type: 'docSidebar',
          sidebarId: 'guideSidebar',
          position: 'left',
          label: 'Docs',
        },
        {
          href: 'https://github.com/MarcelRoozekrans/Mnemosyne.NET',
          label: 'GitHub',
          position: 'right',
        },
        {
          href: 'https://www.nuget.org/packages/Mnemosyne.NET',
          label: 'NuGet',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Docs',
          items: [{ label: 'Docs', to: '/' }],
        },
        {
          title: 'More',
          items: [
            {
              label: 'GitHub',
              href: 'https://github.com/MarcelRoozekrans/Mnemosyne.NET',
            },
            {
              label: 'NuGet',
              href: 'https://www.nuget.org/packages/Mnemosyne.NET',
            },
          ],
        },
      ],
      copyright: `Copyright © ${new Date().getFullYear()} Mnemosyne.NET. Built with Docusaurus.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['csharp', 'bash', 'json', 'yaml'],
    },
    mermaid: {
      theme: { light: 'neutral', dark: 'dark' },
    },
  } satisfies Preset.ThemeConfig,
};

export default config;

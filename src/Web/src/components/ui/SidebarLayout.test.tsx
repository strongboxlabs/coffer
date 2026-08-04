import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';

import {
    MainArea,
    MainPane,
    Sidebar,
    SidebarFooter,
    SidebarHeader,
    SidebarLayout,
    SidebarNav,
    SidebarNavLink,
    SidebarPicker,
    SidebarSection,
    TopBar,
} from './SidebarLayout';

// Smoke tests for the sidebar shell primitives. These are pure
// styled wrappers — the structural behaviour we lock down is
// "children render in the right slots" and "active state on
// SidebarNavLink sets aria-current=page", which is what consumers
// will assert against.

describe('SidebarLayout primitives', () => {
    it('composes the full shell', () => {
        render(
            <SidebarLayout>
                <Sidebar>
                    <SidebarHeader>
                        <span>Coffer</span>
                    </SidebarHeader>
                    <SidebarPicker>Family</SidebarPicker>
                    <SidebarNav>
                        <SidebarNavLink href="#a" active>
                            Dashboard
                        </SidebarNavLink>
                        <SidebarNavLink href="#b">All transactions</SidebarNavLink>
                        <SidebarSection>Accounts · Banking</SidebarSection>
                        <SidebarNavLink href="#c">Eastbank Checking</SidebarNavLink>
                    </SidebarNav>
                    <SidebarFooter>
                        <span>Alice</span>
                    </SidebarFooter>
                </Sidebar>
                <MainArea>
                    <TopBar>
                        <span>Family · Dashboard</span>
                    </TopBar>
                    <MainPane>
                        <p>main content</p>
                    </MainPane>
                </MainArea>
            </SidebarLayout>,
        );

        expect(screen.getByText('Coffer')).toBeInTheDocument();
        expect(screen.getByText('Family')).toBeInTheDocument();
        expect(screen.getByText('Alice')).toBeInTheDocument();
        expect(screen.getByText('main content')).toBeInTheDocument();
        expect(screen.getByText('Accounts · Banking')).toBeInTheDocument();
        expect(screen.getByText('Family · Dashboard')).toBeInTheDocument();
    });

    it('sets aria-current=page on the active nav link', () => {
        render(
            <SidebarLayout>
                <Sidebar>
                    <SidebarNav>
                        <SidebarNavLink href="#a" active>
                            Active
                        </SidebarNavLink>
                        <SidebarNavLink href="#b">Inactive</SidebarNavLink>
                    </SidebarNav>
                </Sidebar>
            </SidebarLayout>,
        );

        expect(screen.getByText('Active')).toHaveAttribute('aria-current', 'page');
        expect(screen.getByText('Inactive')).not.toHaveAttribute('aria-current');
    });
});
